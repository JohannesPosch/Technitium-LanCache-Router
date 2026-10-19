using System.Collections.Concurrent;
using System.Net;
using LanCacheRouter.Config;
using LanCacheRouter.Health;
using LanCacheRouter.Health.Probes;
using Microsoft.Extensions.Time.Testing;

namespace LanCacheRouter.Tests.Health;

public class HealthMonitorTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly ScriptedProbe _probe = new();
    private readonly HealthMonitor _monitor;

    public HealthMonitorTests()
    {
        _monitor = new HealthMonitor(_logs.Enqueue, _ => _probe, _time);
    }

    public void Dispose() => _monitor.Dispose();

    /// <summary>
    /// A probe whose outcome is decided by the test and that counts its calls per address.
    /// </summary>
    private sealed class ScriptedProbe : IProbe
    {
        private readonly ConcurrentDictionary<string, int> _calls = new();

        public Func<IPAddress, bool> Decide { get; set; } = _ => true;

        public bool Block { get; set; }

        public int Calls(string address) => _calls.GetValueOrDefault(address);

        public int TotalCalls => _calls.Values.Sum();

        public async Task<ProbeResult> ProbeAsync(IPAddress address, ResolvedHealthCheck check, CancellationToken cancellationToken)
        {
            _calls.AddOrUpdate(address.ToString(), 1, (_, count) => count + 1);

            if (Block)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            bool ok = Decide(address);
            return new ProbeResult(ok, ok ? "scripted ok" : "scripted failure");
        }
    }

    private static RouterConfig Config(string servers, string healthCheckDefaults = "")
    {
        string defaults = healthCheckDefaults.Length == 0
            ? ""
            : $"\"healthCheckDefaults\": {{ {healthCheckDefaults} }},";

        ConfigParseResult result = ConfigParser.Parse($$"""{ {{defaults}} "cacheServers": [ {{servers}} ] }""");
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.Config!;
    }

    private static string Server(string name, string address, string extra = "")
        => $$"""{ "name": "{{name}}", "address": "{{address}}" {{extra}} }""";

    /// <summary>
    /// Lets background probe loops run: waits a little in real time, then advances the fake clock by one second, until the condition holds.
    /// </summary>
    private async Task<bool> PumpUntil(Func<bool> condition, int maxSeconds = 120)
    {
        for (int i = 0; i <= maxSeconds; i++)
        {
            await Task.Delay(15);
            if (condition())
                return true;

            _time.Advance(TimeSpan.FromSeconds(1));
        }

        return false;
    }

    /// <summary>
    /// Waits a short real time so already-runnable background work can finish, without moving the fake clock.
    /// </summary>
    private static Task Settle() => Task.Delay(100);

    [Fact]
    public async Task StartupProbe_ProbesImmediately_AndReachesHealthyWithinFastIntervals()
    {
        RouterConfig config = Config(Server("s", "10.0.0.1"), "\"intervalSeconds\": 10, \"successThreshold\": 3");
        DateTimeOffset start = _time.GetUtcNow();

        _monitor.Apply(config);
        await Settle();
        Assert.True(_probe.TotalCalls >= 1, "first probe must not wait for an interval");

        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.IsUsable));

        // 3 successes at 1 s fast interval: far below the 10 s normal interval.
        Assert.True(_time.GetUtcNow() - start < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task UnknownAddress_IsNotServed()
    {
        _probe.Block = true;

        _monitor.Apply(Config(Server("s", "10.0.0.1")));
        await Settle();

        ServerHealth health = _monitor.Snapshot.Get("s")!;
        Assert.Equal(HealthState.Unknown, health.State);
        Assert.False(health.IsUsable);
    }

    [Fact]
    public async Task FailingServer_BecomesUnhealthy_AndRecovers()
    {
        _probe.Decide = _ => false;
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"failThreshold\": 2, \"successThreshold\": 3"));

        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.State == HealthState.Unhealthy));
        Assert.False(_monitor.Snapshot.Get("s")!.IsUsable);

        _probe.Decide = _ => true;

        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.State == HealthState.Healthy));
        Assert.True(_monitor.Snapshot.Get("s")!.IsUsable);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), _monitor.Snapshot.Get("s")!.Address);
    }

    [Fact]
    public async Task HealthyServer_GoesUnhealthy_WhenProbesStartFailing()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"failThreshold\": 2, \"successThreshold\": 1"));
        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.IsUsable));

        _probe.Decide = _ => false;

        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.State == HealthState.Unhealthy));
    }

    [Fact]
    public async Task DisabledServer_IsNotProbed_AndReportedDisabled()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1", ", \"enabled\": false")));
        await PumpUntil(() => false, maxSeconds: 30);

        Assert.Equal(HealthState.Disabled, _monitor.Snapshot.Get("s")!.State);
        Assert.Equal(0, _probe.TotalCalls);
    }

    [Fact]
    public async Task Servers_AreTrackedIndependently()
    {
        _probe.Decide = address => address.ToString() == "10.0.0.2";
        _monitor.Apply(Config(
            Server("s", "10.0.0.1") + "," + Server("t", "10.0.0.2"),
            "\"failThreshold\": 1, \"successThreshold\": 1"));

        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("t")!.IsUsable && _monitor.Snapshot.Get("s")!.State == HealthState.Unhealthy));
        Assert.Equal(IPAddress.Parse("10.0.0.2"), _monitor.Snapshot.Get("t")!.Address);
    }

    [Fact]
    public async Task ChangedAddress_ResetsToUnknown_AndProbesTheNewAddress()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"successThreshold\": 1"));
        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.IsUsable));

        _probe.Block = true;
        _monitor.Apply(Config(Server("s", "10.0.0.5"), "\"successThreshold\": 1"));

        Assert.Equal(HealthState.Unknown, _monitor.Snapshot.Get("s")!.State);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), _monitor.Snapshot.Get("s")!.Address);
        await Settle();
        Assert.True(_probe.Calls("10.0.0.5") >= 1);
    }

    [Fact]
    public async Task ReApplyingSameConfig_KeepsHealthState()
    {
        string servers = Server("s", "10.0.0.1");
        _monitor.Apply(Config(servers, "\"successThreshold\": 1"));
        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.IsUsable));

        _monitor.Apply(Config(servers, "\"successThreshold\": 1"));

        Assert.True(_monitor.Snapshot.Get("s")!.IsUsable, "unchanged server must not fall back to Unknown");
    }

    [Fact]
    public async Task ChangedHealthCheck_ResetsToUnknown()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"successThreshold\": 1"));
        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.IsUsable));

        _probe.Block = true;
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"successThreshold\": 1, \"port\": 8080"));

        Assert.Equal(HealthState.Unknown, _monitor.Snapshot.Get("s")!.State);
    }

    [Fact]
    public async Task RemovedServer_StopsBeingProbed_AndLeavesTheSnapshot()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1") + "," + Server("t", "10.0.0.9")));
        await PumpUntil(() => _probe.Calls("10.0.0.9") >= 1);

        _monitor.Apply(Config(Server("s", "10.0.0.1")));
        await Settle();
        int before = _probe.Calls("10.0.0.9");
        await PumpUntil(() => false, maxSeconds: 30);

        Assert.Null(_monitor.Snapshot.Get("t"));
        Assert.Equal(before, _probe.Calls("10.0.0.9"));
    }

    [Fact]
    public async Task ReEnabledServer_StartsAgainAsUnknown()
    {
        string servers = Server("s", "10.0.0.1");
        _monitor.Apply(Config(servers, "\"successThreshold\": 1"));
        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.IsUsable));

        _monitor.Apply(Config(Server("s", "10.0.0.1", ", \"enabled\": false")));
        Assert.Equal(HealthState.Disabled, _monitor.Snapshot.Get("s")!.State);

        _probe.Block = true;
        _monitor.Apply(Config(servers, "\"successThreshold\": 1"));
        Assert.Equal(HealthState.Unknown, _monitor.Snapshot.Get("s")!.State);
    }

    [Fact]
    public async Task StateChanges_AreLogged_ProbeResultsOnlyWhenVerbose()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"successThreshold\": 1"));
        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.IsUsable));
        await Settle();

        Assert.Contains(_logs, l => l == "Cache server 's' (10.0.0.1): initial state Unknown");
        Assert.Contains(_logs, l => l == "Cache server 's' (10.0.0.1): Unknown -> Healthy");
        Assert.DoesNotContain(_logs, l => l.StartsWith("Health probe"));
    }

    [Fact]
    public async Task Verbose_LogsEveryProbe()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"verbose\": true"));
        Assert.True(await PumpUntil(() => _logs.Any(l => l.StartsWith("Health probe s/10.0.0.1: OK (scripted ok)"))));
    }

    [Fact]
    public async Task ProbeException_CountsAsFailure()
    {
        _probe.Decide = _ => throw new InvalidOperationException("boom");
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"failThreshold\": 1"));

        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.State == HealthState.Unhealthy));
    }

    [Fact]
    public async Task Dispose_StopsProbing()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1")));
        await PumpUntil(() => _probe.TotalCalls >= 1);

        _monitor.Dispose();
        await Settle();
        int before = _probe.TotalCalls;
        await PumpUntil(() => false, maxSeconds: 30);

        Assert.Equal(before, _probe.TotalCalls);
    }

    [Fact]
    public async Task Apply_AfterDispose_IsIgnored()
    {
        _monitor.Dispose();

        _monitor.Apply(Config(Server("s", "10.0.0.1")));
        await PumpUntil(() => false, maxSeconds: 5);

        Assert.Equal(0, _probe.TotalCalls);
        Assert.Null(_monitor.Snapshot.Get("s"));
    }

    [Fact]
    public void BeforeAnyConfiguration_TheSnapshotIsEmpty_AndDisposeIsHarmless()
    {
        Assert.Empty(_monitor.Snapshot.Servers);

        _monitor.Dispose();
        _monitor.Dispose();
    }

    [Fact]
    public async Task ProbeThatDoesNotAnswerInTime_CountsAsFailure()
    {
        // The timeout runs on the real clock (it limits the probe's I/O), so this test waits about a second.
        _probe.Block = true;
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"intervalSeconds\": 1, \"timeoutSeconds\": 1, \"failThreshold\": 1, \"verbose\": true"));

        for (int i = 0; i < 400 && _monitor.Snapshot.Get("s")!.State != HealthState.Unhealthy; i++)
            await Task.Delay(10);

        Assert.Equal(HealthState.Unhealthy, _monitor.Snapshot.Get("s")!.State);
        Assert.Contains(_logs, l => l == "Health probe s/10.0.0.1: FAILED (timeout after 1s)");
    }

    [Fact]
    public async Task AfterLeavingUnknown_ProbesRunAtTheNormalInterval_NotTheFastOne()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"intervalSeconds\": 10, \"successThreshold\": 1"));
        await Settle();
        Assert.True(_monitor.Snapshot.Get("s")!.IsUsable);
        Assert.Equal(1, _probe.TotalCalls);

        _time.Advance(TimeSpan.FromSeconds(9));
        await Settle();
        Assert.Equal(1, _probe.TotalCalls);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Settle();
        Assert.Equal(2, _probe.TotalCalls);
    }

    [Fact]
    public async Task WhileUnknown_ProbesRunAtTheConfiguredFastInterval()
    {
        _probe.Decide = _ => false;
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"intervalSeconds\": 30, \"failThreshold\": 99, \"startupProbe\": { \"fastIntervalSeconds\": 2 }"));
        await Settle();
        Assert.Equal(1, _probe.TotalCalls);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Settle();
        Assert.Equal(1, _probe.TotalCalls);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Settle();
        Assert.Equal(2, _probe.TotalCalls);
        Assert.Equal(HealthState.Unknown, _monitor.Snapshot.Get("s")!.State);
    }

    [Fact]
    public async Task StartupProbeDisabled_DelaysTheFirstProbe_AndNeverUsesTheFastInterval()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"intervalSeconds\": 10, \"successThreshold\": 3, \"startupProbe\": { \"enabled\": false }"));
        await Settle();
        Assert.Equal(0, _probe.TotalCalls); // waits for a random part of the interval first

        Assert.True(await PumpUntil(() => _probe.TotalCalls == 1, maxSeconds: 11)); // the random delay is shorter than the interval
        Assert.Equal(HealthState.Unknown, _monitor.Snapshot.Get("s")!.State);

        for (int i = 0; i < 5; i++) // still Unknown, but the fast interval (1 s) must not apply
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Settle();
        }

        Assert.Equal(1, _probe.TotalCalls);
        Assert.True(await PumpUntil(() => _probe.TotalCalls == 2, maxSeconds: 12));
    }

    [Fact]
    public async Task ChangedAddress_StopsProbingTheOldAddress()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"successThreshold\": 1"));
        Assert.True(await PumpUntil(() => _probe.Calls("10.0.0.1") >= 2));

        _monitor.Apply(Config(Server("s", "10.0.0.5"), "\"successThreshold\": 1"));
        await Settle();
        int oldCalls = _probe.Calls("10.0.0.1");
        await PumpUntil(() => false, maxSeconds: 40);

        Assert.Equal(oldCalls, _probe.Calls("10.0.0.1"));
        Assert.True(_probe.Calls("10.0.0.5") >= 3);
    }

    [Fact]
    public async Task EveryStateChange_IsLogged_IncludingTheResetCausedByAConfigChange()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"successThreshold\": 1, \"failThreshold\": 1"));
        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.IsUsable));

        _probe.Decide = _ => false;
        Assert.True(await PumpUntil(() => _monitor.Snapshot.Get("s")!.State == HealthState.Unhealthy));

        _probe.Block = true;
        _monitor.Apply(Config(Server("s", "10.0.0.5"), "\"successThreshold\": 1, \"failThreshold\": 1"));
        _monitor.Apply(Config(Server("s", "10.0.0.5", ", \"enabled\": false")));

        string[] lines = [.. _logs];
        Assert.Contains("Cache server 's' (10.0.0.1): Unknown -> Healthy", lines);
        Assert.Contains("Cache server 's' (10.0.0.1): Healthy -> Unhealthy", lines);
        Assert.Contains("Cache server 's' (10.0.0.5): Unhealthy -> Unknown", lines);
        Assert.Contains("Cache server 's' (10.0.0.5): Unknown -> Disabled", lines);
    }

    [Fact]
    public async Task VerboseLog_ShowsTheReasonOfEachFailure()
    {
        _probe.Decide = _ => false;
        _monitor.Apply(Config(Server("s", "10.0.0.1"), "\"verbose\": true"));

        Assert.True(await PumpUntil(() => _logs.Contains("Health probe s/10.0.0.1: FAILED (scripted failure)")));
    }

    [Fact]
    public async Task ProbeTypeNone_IsHealthyAtOnce_AndNeverProbed()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1", ", \"healthCheck\": { \"type\": \"none\" }")));

        Assert.Equal(HealthState.Healthy, _monitor.Snapshot.Get("s")!.State); // no waiting for thresholds
        await PumpUntil(() => false, maxSeconds: 60);
        Assert.Equal(0, _probe.TotalCalls);
        Assert.Equal(HealthState.Healthy, _monitor.Snapshot.Get("s")!.State);
    }

    [Fact]
    public async Task ProbeTypeNone_AfterBeingDisabledAndReEnabled_IsHealthyAgainAtOnce()
    {
        string enabled = Server("s", "10.0.0.1", ", \"healthCheck\": { \"type\": \"none\" }");
        _monitor.Apply(Config(enabled));
        _monitor.Apply(Config(Server("s", "10.0.0.1", ", \"enabled\": false, \"healthCheck\": { \"type\": \"none\" }")));
        Assert.Equal(HealthState.Disabled, _monitor.Snapshot.Get("s")!.State);

        _monitor.Apply(Config(enabled));
        await Settle();

        Assert.Equal(HealthState.Healthy, _monitor.Snapshot.Get("s")!.State);
    }

    [Fact]
    public async Task SwitchingAServerFromNoneToARealProbe_StartsItAsUnknown()
    {
        _monitor.Apply(Config(Server("s", "10.0.0.1", ", \"healthCheck\": { \"type\": \"none\" }")));
        Assert.Equal(HealthState.Healthy, _monitor.Snapshot.Get("s")!.State);

        _probe.Block = true;
        _monitor.Apply(Config(Server("s", "10.0.0.1", ", \"healthCheck\": { \"type\": \"tcp\" }")));
        await Settle();

        Assert.Equal(HealthState.Unknown, _monitor.Snapshot.Get("s")!.State);
    }

    [Fact]
    public async Task ALoggerThatThrows_DoesNotBreakConfigurationOrFreezeTheStateMachine()
    {
        using HealthMonitor monitor = new(_ => throw new IOException("log disk full"), _ => _probe, _time);

        monitor.Apply(Config(Server("s", "10.0.0.1"), "\"verbose\": true, \"successThreshold\": 1, \"failThreshold\": 1"));
        Assert.True(await PumpUntil(() => monitor.Snapshot.Get("s")!.IsUsable));

        _probe.Decide = _ => false;

        Assert.True(await PumpUntil(() => monitor.Snapshot.Get("s")!.State == HealthState.Unhealthy)); // a stale "Healthy" must never survive
    }

    [Fact]
    public async Task ManyServers_AreTrackedIndependently()
    {
        _probe.Decide = address => address.GetAddressBytes()[3] % 2 == 0;
        string servers = string.Join(",", Enumerable.Range(1, 200).Select(i => Server($"s{i}", $"10.0.{i / 250}.{i % 250 + 1}")));
        _monitor.Apply(Config(servers, "\"successThreshold\": 1, \"failThreshold\": 1"));

        Assert.True(await PumpUntil(() => _monitor.Snapshot.Servers.All(s => s.State is HealthState.Healthy or HealthState.Unhealthy), maxSeconds: 30));

        Assert.Equal(200, _monitor.Snapshot.Servers.Count);
        Assert.All(_monitor.Snapshot.Servers, s => Assert.Equal(s.Address.GetAddressBytes()[3] % 2 == 0, s.IsUsable));
    }

    [Fact]
    public async Task ConcurrentConfigurationStorm_NeitherDeadlocksNorLeaksProbeLoops()
    {
        string[] configs =
        [
            Server("a", "10.0.0.1") + "," + Server("b", "10.0.0.2"),
            Server("a", "10.0.0.1") + "," + Server("b", "10.0.0.3"),
            Server("a", "10.0.0.1", ", \"enabled\": false") + "," + Server("c", "10.0.0.4"),
            Server("b", "10.0.0.2", ", \"healthCheck\": { \"intervalSeconds\": 20 }"),
        ];
        RouterConfig[] parsed = [.. configs.Select(c => Config(c, "\"successThreshold\": 1"))];
        using CancellationTokenSource stop = new();
        Task clock = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }
        });

        Parallel.For(0, 400, i => _monitor.Apply(parsed[i % parsed.Length]));
        _monitor.Apply(parsed[1]); // the final, sequential save
        await Settle();
        stop.Cancel();
        await clock;

        Assert.Equal(["a", "b"], _monitor.Snapshot.Servers.Select(s => s.Name).Order());
        Assert.Equal(IPAddress.Parse("10.0.0.3"), _monitor.Snapshot.Get("b")!.Address);

        _monitor.Dispose();
        await Settle();
        int calls = _probe.TotalCalls;
        _time.Advance(TimeSpan.FromMinutes(5));
        await Settle();
        Assert.Equal(calls, _probe.TotalCalls); // every loop, including those of replaced workers, has ended
    }
}
