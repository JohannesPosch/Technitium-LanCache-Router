using Cronos;
using LanCacheRouter.Config;

namespace LanCacheRouter.DomainList;

/// <summary>
/// Keeps the active <see cref="DomainMatcher"/> up to date (ADR 0002): loads the persisted list at start, downloads a fresh
/// one on a cron schedule, and swaps the matcher atomically. A refresh is applied whenever the whole download succeeded;
/// if anything fails, the previous matcher stays active and the refresh is retried after <see cref="RetryDelay"/>.
/// </summary>
public sealed class DomainListManager : IDisposable
{
    /// <summary>
    /// How long to wait before retrying after a failed refresh (or until the next cron time, whichever comes first).
    /// </summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default maximum random delay added to scheduled refreshes, so the nodes of a cluster, which share one cron
    /// expression, do not all hit GitHub in the same second.
    /// </summary>
    public static readonly TimeSpan DefaultMaxJitter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Longest single wait for the next refresh. <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> cannot wait
    /// longer than about 49 days at once, which a quarterly or yearly schedule needs, so long waits are made in steps.
    /// </summary>
    private static readonly TimeSpan MaxWaitStep = TimeSpan.FromDays(1);

    private readonly object _gate = new();
    private readonly IDomainSource _source;
    private readonly DomainSnapshotStore? _store;
    private readonly Action<string> _log;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _jitter;
    private DomainListConfig? _config;
    private RawDomainList? _raw;
    private DomainMatcher _matcher = DomainMatcher.Empty;
    private CancellationTokenSource? _loop;
    private long _generation;
    private bool _disposed;

    /// <summary>
    /// Creates a manager that is idle until <see cref="Apply"/> is called.
    /// </summary>
    /// <param name="source">Performs the downloads.</param>
    /// <param name="store">Persists the last good list, or <c>null</c> to keep it in memory only.</param>
    /// <param name="log">Receives log lines (refresh results, failures, filter warnings).</param>
    /// <param name="timeProvider">Clock for the schedule; defaults to the system clock. Cron times use <see cref="TimeProvider.LocalTimeZone"/>.</param>
    /// <param name="maxJitter">Upper bound of the random delay added to scheduled refreshes; defaults to <see cref="DefaultMaxJitter"/>.</param>
    public DomainListManager(
        IDomainSource source,
        DomainSnapshotStore? store,
        Action<string> log,
        TimeProvider? timeProvider = null,
        TimeSpan? maxJitter = null)
    {
        _source = source;
        _store = store;
        _log = SafeLog.Wrap(log);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitter = TimeSpan.FromSeconds(Random.Shared.NextDouble() * (maxJitter ?? DefaultMaxJitter).TotalSeconds);
    }

    /// <summary>
    /// The current matcher. Lock-free and safe to call from the request path. Empty until a list is available.
    /// </summary>
    public DomainMatcher Matcher => Volatile.Read(ref _matcher);

    /// <summary>
    /// When the list behind the current matcher was downloaded, or <c>null</c> if there is none yet.
    /// </summary>
    public DateTimeOffset? ListFetchedAt
    {
        get
        {
            lock (_gate)
                return _raw?.FetchedAt;
        }
    }

    /// <summary>
    /// Applies a (new) configuration; safe to call on every config save. The filters take effect immediately from the
    /// list already in memory (or the persisted snapshot). A download starts at once if this is the first call and
    /// <c>refreshOnStartup</c> is set, if the base URL changed, or if there is no list at all. The cron schedule restarts.
    /// </summary>
    /// <param name="config">The validated domain-list settings.</param>
    public void Apply(DomainListConfig config)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            bool first = _config is null;
            bool urlChanged = _config is not null && !SameBaseUrl(_config.BaseUrl, config.BaseUrl);

            _loop?.Cancel();
            _loop = new CancellationTokenSource();
            long generation = ++_generation;
            _config = config;

            // After a source change, prefer a persisted list of the new source; otherwise keep serving the list in memory
            // (domains of the old source are still cache domains) until the download from the new source succeeds.
            if (_raw is null || urlChanged)
                _raw = LoadSnapshot(config) ?? _raw;

            Rebuild(config);

            bool refreshNow = _raw is null || urlChanged || (first && config.RefreshOnStartup);
            CancellationToken token = _loop.Token;
            _ = Task.Run(() => RunAsync(config, generation, refreshNow, token));
        }
    }

    /// <summary>
    /// Stops the schedule and all network activity, for example while the app is disabled. The manager stays usable:
    /// the next <see cref="Apply"/> starts again as if for the first time, including the startup refresh.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            _loop?.Cancel();
            _loop = null;
            _generation++;
            _config = null;
        }
    }

    /// <summary>
    /// Stops the schedule for good. The last matcher stays readable.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _loop?.Cancel();
        }
    }

    /// <summary>
    /// Compares two base URLs ignoring a trailing slash and case of the host.
    /// </summary>
    private static bool SameBaseUrl(string a, string b)
        => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the persisted list if it came from the configured source. The caller must hold the lock.
    /// </summary>
    /// <param name="config">The settings whose base URL the snapshot must match.</param>
    /// <returns>The persisted list, or <c>null</c> if there is none or it is from another source.</returns>
    private RawDomainList? LoadSnapshot(DomainListConfig config)
    {
        RawDomainList? snapshot = _store?.Load();
        if (snapshot is null)
            return null;

        if (!SameBaseUrl(snapshot.BaseUrl, config.BaseUrl))
        {
            _log($"Domain list: ignoring persisted snapshot from {snapshot.BaseUrl}, configured source is {config.BaseUrl}");
            return null;
        }

        _log($"Domain list: loaded persisted snapshot from {snapshot.FetchedAt:u}");
        return snapshot;
    }

    /// <summary>
    /// Builds a matcher from the list in memory and the given filters and publishes it. The caller must hold the lock.
    /// </summary>
    /// <param name="config">The settings with the filters.</param>
    private void Rebuild(DomainListConfig config)
    {
        if (_raw is null)
            return;

        DomainMatcher matcher = DomainMatcher.Create(_raw, config, _log);
        Volatile.Write(ref _matcher, matcher);
        _log($"Domain list: {matcher.Count} patterns active in {matcher.ServiceCounts.Count} service(s)");
    }

    /// <summary>
    /// Refresh loop of one configuration: optional immediate refresh, then one refresh per cron occurrence (plus jitter),
    /// with a retry after failures. Ends when <paramref name="cancellationToken"/> is cancelled, which happens on the next
    /// <see cref="Apply"/> or on dispose. An unexpected error is logged and the loop carries on after <see cref="RetryDelay"/>,
    /// because a loop that dies silently would leave the list to go stale unnoticed.
    /// </summary>
    private async Task RunAsync(DomainListConfig config, long generation, bool refreshNow, CancellationToken cancellationToken)
    {
        try
        {
            CronExpression cron = CronExpression.Parse(
                config.RefreshCron,
                config.RefreshCron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);

            DateTimeOffset? retryAt = null;

            if (refreshNow)
            {
                bool ok = await RefreshAsync(config, generation, cancellationToken);
                retryAt = ok ? null : _timeProvider.GetUtcNow() + RetryDelay;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    DateTimeOffset? scheduled = cron.GetNextOccurrence(now, _timeProvider.LocalTimeZone) is { } next ? next + _jitter : null;

                    DateTimeOffset? due = (retryAt, scheduled) switch
                    {
                        (null, null) => null,
                        (null, _) => scheduled,
                        (_, null) => retryAt,
                        _ => retryAt < scheduled ? retryAt : scheduled
                    };

                    if (due is null)
                    {
                        _log("Domain list: refreshCron has no upcoming occurrence, so the list is only refreshed when the source changes or the app is configured again");
                        return;
                    }

                    await WaitUntilAsync(due.Value, cancellationToken);

                    bool refreshed = await RefreshAsync(config, generation, cancellationToken);
                    retryAt = refreshed ? null : _timeProvider.GetUtcNow() + RetryDelay;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"Domain list: unexpected error in the refresh loop, trying again in {RetryDelay.TotalMinutes:0} min: {ex}");
                    retryAt = null;
                    await Task.Delay(RetryDelay, _timeProvider, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer configuration, or disposed.
        }
        catch (Exception ex)
        {
            _log($"Domain list: the refresh loop stopped unexpectedly, the list is not refreshed until the configuration is saved again: {ex}");
        }
    }

    /// <summary>
    /// Waits until the given time, in steps of at most <see cref="MaxWaitStep"/>.
    /// </summary>
    /// <param name="due">The time to wait for.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    private async Task WaitUntilAsync(DateTimeOffset due, CancellationToken cancellationToken)
    {
        TimeSpan remaining;

        while ((remaining = due - _timeProvider.GetUtcNow()) > TimeSpan.Zero)
            await Task.Delay(remaining < MaxWaitStep ? remaining : MaxWaitStep, _timeProvider, cancellationToken);
    }

    /// <summary>
    /// Downloads the list and, if that fully succeeded and the configuration has not been replaced meanwhile, swaps it in.
    /// </summary>
    /// <returns><c>true</c> if the list was refreshed; <c>false</c> if the download failed (logged) and the old list stays.</returns>
    private async Task<bool> RefreshAsync(DomainListConfig config, long generation, CancellationToken cancellationToken)
    {
        RawDomainList fetched;

        try
        {
            fetched = await DomainListFetcher.FetchAsync(
                config.BaseUrl,
                TimeSpan.FromSeconds(config.HttpTimeoutSeconds),
                _source,
                _log,
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log($"Domain list: refresh failed, keeping the previous list ({(_raw is null ? "none available yet" : $"from {_raw.FetchedAt:u}")}); retrying in {RetryDelay.TotalMinutes:0} min or at the next scheduled time: {ex.Message}");
            return false;
        }

        lock (_gate)
        {
            if (_disposed || generation != _generation)
                return true; // a newer configuration owns the list now; it will refresh by itself if needed

            _raw = fetched;
            _store?.Save(fetched);
            _log($"Domain list: refreshed, {fetched.Services.Sum(s => s.Patterns.Count)} patterns in {fetched.Services.Count} service(s)");
            Rebuild(config);
        }

        return true;
    }
}
