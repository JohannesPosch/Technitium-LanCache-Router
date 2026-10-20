using System.Net;
using LanCacheRouter.Config;
using LanCacheRouter.Health.Probes;

namespace LanCacheRouter.Health;

/// <summary>
/// Probes every enabled cache server in the background and publishes the resulting
/// <see cref="HealthSnapshot"/> (ADR 0003). Replaces lancache-checker. State lives in memory only and is
/// never restored after a restart, so every server starts as Unknown.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ServerWorker> _workers = [];
    private readonly Func<ProbeType, IProbe> _probeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _log;
    private RouterConfig? _config;
    private HealthSnapshot _snapshot = HealthSnapshot.Empty;
    private bool _disposed;

    /// <summary>
    /// Creates a monitor that is idle until <see cref="Apply"/> is called.
    /// </summary>
    /// <param name="log">Receives log lines (state changes, and probe results for servers with verbose enabled).</param>
    /// <param name="probeFactory">Chooses the probe per type. Defaults to <see cref="ProbeFactory.Create"/>; replaced in tests.</param>
    /// <param name="timeProvider">Clock used for probe intervals. Defaults to the system clock; replaced in tests.</param>
    public HealthMonitor(Action<string> log, Func<ProbeType, IProbe>? probeFactory = null, TimeProvider? timeProvider = null)
    {
        _log = SafeLog.Wrap(log);
        _probeFactory = probeFactory ?? ProbeFactory.Create;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The latest published health of all servers. Lock-free and safe to call from the request path.
    /// </summary>
    public HealthSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Applies a (new) configuration. Servers whose address and effective health check are unchanged keep their
    /// state and probe loop; new, changed or re-enabled ones start as Unknown with an immediate probe; removed or
    /// disabled ones stop. Safe to call repeatedly, as Technitium does on every config save.
    /// </summary>
    /// <param name="config">The validated configuration.</param>
    public void Apply(RouterConfig config)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _config = config;

            Dictionary<string, (string Name, IPAddress Address, ResolvedHealthCheck Check)> wanted = [];
            foreach (CacheServerConfig server in config.CacheServers.Where(s => s.Enabled))
                wanted[KeyOf(server.Name)] = (server.Name, IPAddress.Parse(server.Address), config.ResolveHealthCheck(server));

            foreach (string key in _workers.Keys.ToList())
            {
                if (!wanted.TryGetValue(key, out var entry) || !_workers[key].Matches(entry.Address, entry.Check))
                {
                    _workers[key].Stop();
                    _workers.Remove(key);
                }
            }

            List<ServerWorker> added = [];
            foreach ((string key, var entry) in wanted)
            {
                if (_workers.ContainsKey(key))
                    continue;

                ServerWorker worker = new(this, entry.Name, entry.Address, entry.Check);
                _workers[key] = worker;
                added.Add(worker);
            }

            // Publish before starting, so new servers are always reported as Unknown first and every later change is a logged transition.
            PublishLocked();

            foreach (ServerWorker worker in added)
                worker.Start();
        }
    }

    /// <summary>
    /// Stops all probe loops. The last snapshot stays readable.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;

            foreach (ServerWorker worker in _workers.Values)
                worker.Stop();

            _workers.Clear();
        }
    }

    /// <summary>
    /// Builds the dictionary key of a worker.
    /// </summary>
    /// <param name="serverName">The server name; compared ignoring case.</param>
    private static string KeyOf(string serverName) => serverName.ToLowerInvariant();

    /// <summary>
    /// Called by a worker after its server changed state.
    /// </summary>
    private void OnServerChanged()
    {
        lock (_gate)
        {
            if (!_disposed)
                PublishLocked();
        }
    }

    /// <summary>
    /// Recomputes the snapshot from the current worker states, logs state changes and stores it.
    /// The caller must hold the lock.
    /// </summary>
    private void PublishLocked()
    {
        if (_config is null)
            return;

        HealthSnapshot previous = _snapshot;
        List<ServerHealth> servers = [];

        foreach (CacheServerConfig server in _config.CacheServers)
        {
            HealthState state = !server.Enabled
                ? HealthState.Disabled
                : _workers.TryGetValue(KeyOf(server.Name), out ServerWorker? worker) ? worker.State : HealthState.Unknown;

            ServerHealth health = new(server.Name, state, IPAddress.Parse(server.Address));
            servers.Add(health);

            ServerHealth? before = previous.Get(server.Name);
            if (before is null)
                _log($"Cache server '{server.Name}' ({health.Address}): initial state {state}");
            else if (before.State != state)
                _log($"Cache server '{server.Name}' ({health.Address}): {before.State} -> {state}");
        }

        Volatile.Write(ref _snapshot, new HealthSnapshot(servers));
    }

    /// <summary>
    /// Probe loop and <see cref="HealthTracker"/> of one enabled cache server.
    /// </summary>
    private sealed class ServerWorker
    {
        private readonly HealthMonitor _owner;
        private readonly string _serverName;
        private readonly IPAddress _address;
        private readonly ResolvedHealthCheck _check;
        private readonly HealthTracker _tracker;
        private readonly IProbe _probe;
        private readonly CancellationTokenSource _cancellation = new();

        /// <summary>
        /// Creates a worker; call <see cref="Start"/> to begin probing.
        /// </summary>
        /// <param name="owner">The monitor to notify about state changes.</param>
        /// <param name="serverName">Name of the server, for logs.</param>
        /// <param name="address">The address to probe.</param>
        /// <param name="check">The effective health check.</param>
        public ServerWorker(HealthMonitor owner, string serverName, IPAddress address, ResolvedHealthCheck check)
        {
            _owner = owner;
            _serverName = serverName;
            _address = address;
            _check = check;
            _tracker = new HealthTracker(check.FailThreshold, check.SuccessThreshold, check.Type == ProbeType.None ? HealthState.Healthy : HealthState.Unknown);
            _probe = owner._probeFactory(check.Type);
        }

        /// <summary>
        /// The current state of the server.
        /// </summary>
        public HealthState State => _tracker.State;

        /// <summary>
        /// Tells whether this worker already probes the given address with the given check. If not, the server changed
        /// and a new worker (with a fresh Unknown state) must replace this one.
        /// </summary>
        /// <param name="address">The configured address.</param>
        /// <param name="check">The configured effective health check.</param>
        public bool Matches(IPAddress address, ResolvedHealthCheck check) => _address.Equals(address) && _check.Equals(check);

        /// <summary>
        /// Starts the probe loop on the thread pool. A server of type <see cref="ProbeType.None"/> is Healthy from the start and is never probed.
        /// </summary>
        public void Start()
        {
            if (_check.Type != ProbeType.None)
                _ = Task.Run(() => RunAsync(_cancellation.Token));
        }

        /// <summary>
        /// Asks the probe loop to end. A probe that is already running is cancelled; its result is discarded.
        /// </summary>
        public void Stop() => _cancellation.Cancel();

        /// <summary>
        /// Probes until cancelled. With the startup probe enabled, the first probe happens immediately and probing runs at
        /// the fast interval while the state is Unknown; otherwise the first probe is delayed by a random part of the interval
        /// so many servers do not probe in lockstep.
        /// </summary>
        /// <param name="cancellationToken">Cancelled by <see cref="Stop"/>.</param>
        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!_check.StartupProbeEnabled)
                {
                    TimeSpan jitter = TimeSpan.FromSeconds(Random.Shared.NextDouble() * _check.IntervalSeconds);
                    await Task.Delay(jitter, _owner._timeProvider, cancellationToken);
                }

                while (true)
                {
                    ProbeResult result = await ExecuteProbeAsync(cancellationToken);

                    if (_check.Verbose || _owner._config?.Logging.Verbose == true)
                        _owner._log($"Health probe {_serverName}/{_address}: {(result.Success ? "OK" : "FAILED")} ({SafeLog.Escape(result.Detail)})");

                    if (_tracker.RecordResult(result.Success) is not null)
                        _owner.OnServerChanged();

                    bool fast = _check.StartupProbeEnabled && _tracker.State == HealthState.Unknown;
                    await Task.Delay(TimeSpan.FromSeconds(fast ? _check.StartupFastIntervalSeconds : _check.IntervalSeconds), _owner._timeProvider, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped by the monitor.
            }
        }

        /// <summary>
        /// Runs one probe under the configured timeout. A timeout or an unexpected exception counts as a failed probe.
        /// </summary>
        /// <param name="cancellationToken">The worker's token; cancellation by it is propagated, not treated as a failure.</param>
        private async Task<ProbeResult> ExecuteProbeAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_check.TimeoutSeconds));

            try
            {
                return await _probe.ProbeAsync(_address, _check, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ProbeResult(false, $"timeout after {_check.TimeoutSeconds}s");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ProbeResult(false, ex.Message);
            }
        }
    }
}
