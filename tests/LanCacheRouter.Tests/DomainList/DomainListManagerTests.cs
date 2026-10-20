using System.Collections.Concurrent;
using LanCacheRouter.Config;
using LanCacheRouter.DomainList;
using Microsoft.Extensions.Time.Testing;

namespace LanCacheRouter.Tests.DomainList;

public sealed class DomainListManagerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero));
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lancacherouter-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDomainSource _source = FakeDomainSource.WithSteamAndEpic();
    private readonly DomainSnapshotStore _store;
    private DomainListManager? _manager;

    public DomainListManagerTests()
    {
        _store = new DomainSnapshotStore(Path.Combine(_directory, "snapshot.json"), _logs.Enqueue);
    }

    public void Dispose()
    {
        _manager?.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private DomainListManager Manager(DomainSnapshotStore? store = null)
        => _manager = new DomainListManager(_source, store ?? _store, _logs.Enqueue, _time, maxJitter: TimeSpan.Zero);

    private static DomainListConfig Config(string json = "{}")
    {
        ConfigParseResult result = ConfigParser.Parse($$"""{ "domainList": {{WithBaseUrl(json)}} }""");
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.Config!.DomainList;
    }

    /// <summary>Points the config at the fake repository unless it names its own baseUrl.</summary>
    private static string WithBaseUrl(string json)
        => json.Contains("baseUrl") ? json : json.TrimEnd('}').TrimEnd() + (json.Trim() == "{}" ? "" : ",") + $" \"baseUrl\": \"{FakeDomainSource.BaseUrl}\" }}";

    private static RawDomainList Snapshot(params string[] patterns)
        => new(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero), FakeDomainSource.BaseUrl, [new ServiceDomains("persisted", patterns)]);

    /// <summary>
    /// Lets the background loop run: waits a little in real time, then moves the fake clock forward, until the condition holds.
    /// </summary>
    private async Task<bool> PumpUntil(Func<bool> condition, TimeSpan step, int maxSteps = 400)
    {
        for (int i = 0; i <= maxSteps; i++)
        {
            await Task.Delay(15);
            if (condition())
                return true;

            _time.Advance(step);
        }

        return false;
    }

    private static readonly TimeSpan NoTime = TimeSpan.Zero;

    [Fact]
    public async Task FirstApply_WithRefreshOnStartup_DownloadsAndActivatesTheList()
    {
        Manager().Apply(Config());

        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, NoTime));
        Assert.Equal("steam", _manager!.Matcher.Match("cdn.steamcontent.com"));
        Assert.Equal("epicgames", _manager.Matcher.Match("x.download.epicgames.com"));
        Assert.Equal(_time.GetUtcNow(), _manager.ListFetchedAt);
    }

    [Fact]
    public async Task SuccessfulRefresh_IsPersisted()
    {
        Manager().Apply(Config());
        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, NoTime));

        RawDomainList? saved = _store.Load();

        Assert.NotNull(saved);
        Assert.Equal(["steam", "epicgames"], saved.Services.Select(s => s.Name));
    }

    [Fact]
    public async Task PersistedSnapshot_IsActiveImmediately_AndWithoutRefreshOnStartupNothingIsDownloaded()
    {
        _store.Save(Snapshot("*.persisted.example"));

        Manager().Apply(Config("""{ "refreshOnStartup": false }"""));
        await Task.Delay(100);

        Assert.Equal("persisted", _manager!.Matcher.Match("x.persisted.example")); // synchronously available after Apply
        Assert.Equal(0, _source.TotalCalls);
    }

    [Fact]
    public async Task PersistedSnapshot_KeepsDnsWorkingWhenGitHubIsDown()
    {
        _store.Save(Snapshot("*.persisted.example"));
        _source.FailAll = true;

        Manager().Apply(Config());
        Assert.True(await PumpUntil(() => _source.TotalCalls >= 1, NoTime));

        Assert.Equal("persisted", _manager!.Matcher.Match("x.persisted.example"));
        Assert.Contains(_logs, l => l.Contains("refresh failed"));
    }

    [Fact]
    public async Task NoListAtAll_RefreshesEvenWithoutRefreshOnStartup()
    {
        Manager().Apply(Config("""{ "refreshOnStartup": false }"""));

        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, NoTime));
    }

    [Fact]
    public async Task SnapshotFromAnotherSource_IsIgnored()
    {
        _store.Save(Snapshot("*.persisted.example") with { BaseUrl = "https://other.test/repo/" });
        _source.FailAll = true;

        Manager().Apply(Config("""{ "refreshOnStartup": false }"""));
        await Task.Delay(100);

        Assert.Equal(0, _manager!.Matcher.Count);
        Assert.Contains(_logs, l => l.Contains("ignoring persisted snapshot"));
    }

    [Fact]
    public async Task CronSchedule_RefreshesAtTheNextOccurrence()
    {
        _store.Save(Snapshot("*.persisted.example"));
        Manager().Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "0 4 * * *" }""")); // now = 03:00 UTC
        await Task.Delay(100);
        Assert.Equal(0, _source.IndexCalls);

        _time.Advance(TimeSpan.FromMinutes(30)); // 03:30, not yet
        await Task.Delay(100);
        Assert.Equal(0, _source.IndexCalls);

        Assert.True(await PumpUntil(() => _source.IndexCalls == 1, TimeSpan.FromMinutes(5), maxSteps: 20)); // passes 04:00
        Assert.True(await PumpUntil(() => _manager!.Matcher.Match("x.download.epicgames.com") is not null, NoTime));
    }

    [Fact]
    public async Task CronSchedule_RepeatsEveryDay()
    {
        Manager().Apply(Config("""{ "refreshOnStartup": true, "refreshCron": "0 4 * * *" }"""));
        Assert.True(await PumpUntil(() => _source.IndexCalls == 1, NoTime));
        await Task.Delay(300); // let the loop compute its next due time before the fake clock jumps, even on a busy machine

        Assert.True(await PumpUntil(() => _source.IndexCalls == 2, TimeSpan.FromMinutes(30), maxSteps: 10)); // 04:00 today
        Assert.True(await PumpUntil(() => _source.IndexCalls == 3, TimeSpan.FromHours(1), maxSteps: 40));  // 04:00 tomorrow
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheOldList_AndRetriesAfterFiveMinutes()
    {
        _store.Save(Snapshot("*.persisted.example"));
        _source.FailAll = true;
        Manager().Apply(Config("""{ "refreshCron": "0 4 * * *" }"""));
        Assert.True(await PumpUntil(() => _source.IndexCalls == 1, NoTime));
        Assert.Equal("persisted", _manager!.Matcher.Match("x.persisted.example"));

        _time.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(100);
        Assert.Equal(1, _source.IndexCalls); // not yet

        _source.FailAll = false;
        Assert.True(await PumpUntil(() => _source.IndexCalls == 2, TimeSpan.FromSeconds(30), maxSteps: 20));

        Assert.True(await PumpUntil(() => _manager.Matcher.Match("x.download.epicgames.com") is not null, NoTime));
        Assert.Null(_manager.Matcher.Match("x.persisted.example")); // replaced by the fresh list
    }

    [Fact]
    public async Task PartialFailure_DoesNotReplaceTheList()
    {
        _store.Save(Snapshot("*.persisted.example"));
        _source.FailSuffix = "epicgames.txt"; // index and steam succeed, epic fails
        Manager().Apply(Config());
        Assert.True(await PumpUntil(() => _source.Calls("epicgames.txt") >= 1, NoTime));
        await Task.Delay(100);

        Assert.Equal("persisted", _manager!.Matcher.Match("x.persisted.example"));
        Assert.Null(_manager.Matcher.Match("cdn.steamcontent.com"));
    }

    [Fact]
    public async Task ReApply_WithNewFilters_RebuildsWithoutDownloading()
    {
        Manager().Apply(Config());
        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, NoTime));
        Assert.Equal("steam", _manager!.Matcher.Match("cdn.steamcontent.com"));
        int calls = _source.TotalCalls;

        _manager.Apply(Config("""{ "services": { "exclude": ["steam"] } }"""));
        await Task.Delay(100);

        Assert.Null(_manager.Matcher.Match("cdn.steamcontent.com")); // applied synchronously
        Assert.NotNull(_manager.Matcher.Match("x.download.epicgames.com"));
        Assert.Equal(calls, _source.TotalCalls);
    }

    private const string MirrorBase = "https://mirror.test/cd/";

    private void ServeMirror()
    {
        _source
            .SetAt(MirrorBase, "cache_domains.json", """{ "cache_domains": [ { "name": "mirror", "domain_files": ["m.txt"] } ] }""")
            .SetAt(MirrorBase, "m.txt", "*.mirror.example\n");
    }

    [Fact]
    public async Task ReApply_WithNewBaseUrl_ReplacesTheListOnceTheNewSourceDelivers()
    {
        Manager().Apply(Config());
        Assert.True(await PumpUntil(() => _manager!.Matcher.Match("cdn.steamcontent.com") is not null, NoTime));

        ServeMirror();
        _manager!.Apply(Config($$"""{ "baseUrl": "{{MirrorBase}}" }"""));

        Assert.True(await PumpUntil(() => _manager.Matcher.Match("x.mirror.example") is not null, NoTime));
        Assert.Null(_manager.Matcher.Match("cdn.steamcontent.com"));
        Assert.Equal(MirrorBase, _store.Load()!.BaseUrl);
    }

    [Fact]
    public async Task ReApply_WithNewBaseUrl_KeepsServingTheOldListWhileTheNewSourceFails()
    {
        Manager().Apply(Config());
        Assert.True(await PumpUntil(() => _manager!.Matcher.Match("cdn.steamcontent.com") is not null, NoTime));
        DateTimeOffset fetchedAt = _manager!.ListFetchedAt!.Value;

        _manager.Apply(Config($$"""{ "baseUrl": "{{MirrorBase}}" }""")); // the mirror serves nothing
        Assert.True(await PumpUntil(() => _logs.Any(l => l.Contains("refresh failed")), NoTime));

        Assert.Equal("steam", _manager.Matcher.Match("cdn.steamcontent.com"));
        Assert.Equal(fetchedAt, _manager.ListFetchedAt);
    }

    [Fact]
    public async Task ReApply_WithNewBaseUrl_PrefersAPersistedListOfThatSource()
    {
        Manager().Apply(Config());
        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, NoTime));

        _store.Save(Snapshot("*.cached-mirror.example") with { BaseUrl = MirrorBase });
        _manager!.Apply(Config($$"""{ "baseUrl": "{{MirrorBase}}" }"""));

        Assert.Equal("persisted", _manager.Matcher.Match("x.cached-mirror.example")); // synchronously, before any download
    }

    [Fact]
    public async Task ReApply_ReplacesTheScheduleOfTheOldConfig()
    {
        _store.Save(Snapshot("*.persisted.example"));
        Manager().Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "0 4 * * *" }"""));
        _manager!.Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "0 6 * * *" }"""));
        await Task.Delay(100);

        _time.Advance(TimeSpan.FromMinutes(90)); // 04:30 – the old schedule would have fired
        await Task.Delay(100);
        Assert.Equal(0, _source.IndexCalls);

        Assert.True(await PumpUntil(() => _source.IndexCalls == 1, TimeSpan.FromMinutes(10), maxSteps: 20)); // 06:00
    }

    [Fact]
    public async Task SixFieldCron_IncludesSeconds()
    {
        _store.Save(Snapshot("*.persisted.example"));
        Manager().Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "30 0 3 * * *" }""")); // 03:00:30 every day
        await Task.Delay(100);

        Assert.True(await PumpUntil(() => _source.IndexCalls == 1, TimeSpan.FromSeconds(10), maxSteps: 10));
    }

    [Fact]
    public async Task Dispose_StopsTheSchedule()
    {
        _store.Save(Snapshot("*.persisted.example"));
        Manager().Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "0 4 * * *" }"""));
        await Task.Delay(100);

        _manager!.Dispose();
        _time.Advance(TimeSpan.FromHours(3));
        await Task.Delay(150);

        Assert.Equal(0, _source.IndexCalls);
        Assert.Equal("persisted", _manager.Matcher.Match("x.persisted.example")); // last matcher stays readable
    }

    [Fact]
    public async Task Downloads_AreConfinedToTheBaseUrl_UnlessTheSecurityFlagIsOff()
    {
        ConfinementRecordingSource restricted = new(_source);
        ManagerWith(restricted).Apply(Config());
        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, NoTime));
        Assert.All(restricted.Confinements, c => Assert.Equal(new Uri(FakeDomainSource.BaseUrl), c));
        _manager!.Dispose();

        ConfinementRecordingSource open = new(_source);
        ManagerWith(open).Apply(Config(), new SecurityConfig { RestrictDownloadRedirects = false });
        Assert.True(await PumpUntil(() => !open.Confinements.IsEmpty && _manager!.Matcher.Count > 0, NoTime));
        Assert.All(open.Confinements, Assert.Null);
    }

    [Fact]
    public async Task ConfiguredLimits_AreEnforced_AndFailTheRefreshLikeAnyOther()
    {
        // The fake repository has two files; the limit allows one.
        Manager().Apply(Config(), new SecurityConfig { MaxDomainFiles = 1 });

        Assert.True(await PumpUntil(() => _logs.Any(l => l.Contains("refresh failed") && l.Contains("limit of 1 files")), NoTime));
        Assert.Equal(0, _manager!.Matcher.Count);

        // Saving the configuration with a higher limit lets the next download through.
        _manager.Apply(Config(), new SecurityConfig { MaxDomainFiles = 2, RestrictDownloadRedirects = true });
        Assert.True(await PumpUntil(() => _manager.Matcher.Count > 0, NoTime));
    }

    [Fact]
    public async Task WorksWithoutAStore()
    {
        _manager = new DomainListManager(_source, store: null, _logs.Enqueue, _time, TimeSpan.Zero);
        _manager.Apply(Config());

        Assert.True(await PumpUntil(() => _manager.Matcher.Count > 0, NoTime));
    }

    private DomainListManager ManagerWith(IDomainSource source, TimeSpan? maxJitter = null, Action<string>? log = null)
        => _manager = new DomainListManager(source, _store, log ?? _logs.Enqueue, _time, maxJitter ?? TimeSpan.Zero);

    /// <summary>Waits (real time) until a condition holds, without moving the fake clock.</summary>
    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 300; i++)
        {
            if (condition())
                return true;

            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task ScheduleLongerThanTaskDelayAllows_StillFires()
    {
        // Task.Delay cannot wait longer than ~49.7 days in one go; a quarterly or yearly cron must not kill the loop.
        _store.Save(Snapshot("*.persisted.example"));
        Manager().Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "0 4 1 6 *" }""")); // June 1st, 151 days from the start
        await Task.Delay(150);

        Assert.Equal(0, _source.IndexCalls);
        Assert.DoesNotContain(_logs, l => l.Contains("unexpected") || l.Contains("stopped"));

        _time.Advance(TimeSpan.FromDays(100));
        await Task.Delay(150);
        Assert.Equal(0, _source.IndexCalls);

        Assert.True(await PumpUntil(() => _source.IndexCalls == 1, TimeSpan.FromHours(12), maxSteps: 150));
        Assert.True(await PumpUntil(() => _manager!.Matcher.Match("x.download.epicgames.com") is not null, NoTime));
    }

    [Fact]
    public async Task CronWithoutUpcomingOccurrence_IsLogged_AndDoesNotCrash()
    {
        _store.Save(Snapshot("*.persisted.example"));

        Manager().Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "0 0 31 2 *" }""")); // February 31st never happens

        Assert.True(await WaitUntil(() => _logs.Any(l => l.Contains("no upcoming"))));
        Assert.Equal("persisted", _manager!.Matcher.Match("x.persisted.example"));
    }

    [Fact]
    public async Task UnexpectedExceptionFromTheSource_IsAFailedRefresh_AndTheLoopSurvives()
    {
        InterceptingDomainSource source = new(_source);
        int attempts = 0;
        source.BeforeRequest = (_, _) => Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("surprise") : Task.CompletedTask;
        ManagerWith(source).Apply(Config("""{ "refreshCron": "0 4 * * *" }"""));

        Assert.True(await WaitUntil(() => _logs.Any(l => l.Contains("refresh failed") && l.Contains("surprise"))));
        await Task.Delay(100);

        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, TimeSpan.FromSeconds(30), maxSteps: 30)); // the retry after 5 minutes
    }

    [Fact]
    public async Task CancellationNotCausedByTheManager_IsAFailedRefresh_NotAShutdown()
    {
        // HttpClient reports its own timeouts as TaskCanceledException; that must not be mistaken for "the app is stopping".
        InterceptingDomainSource source = new(_source);
        int attempts = 0;
        source.BeforeRequest = (_, _) => Interlocked.Increment(ref attempts) == 1 ? throw new TaskCanceledException("http timeout") : Task.CompletedTask;
        ManagerWith(source).Apply(Config());

        Assert.True(await WaitUntil(() => _logs.Any(l => l.Contains("refresh failed"))));
        await Task.Delay(100);

        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, TimeSpan.FromSeconds(30), maxSteps: 30));
    }

    [Fact]
    public async Task ALoggerThatThrows_DoesNotStopRefreshesOrBreakApply()
    {
        void ThrowingLog(string message) => throw new IOException("log disk full");

        ManagerWith(_source, log: ThrowingLog).Apply(Config("""{ "refreshCron": "0 4 * * *" }"""));

        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, NoTime));
        await Task.Delay(300);

        Assert.True(await PumpUntil(() => _source.IndexCalls == 2, TimeSpan.FromMinutes(30), maxSteps: 10)); // the scheduled refresh still happens
    }

    [Fact]
    public async Task DownloadOfASupersededConfiguration_IsNotApplied()
    {
        InterceptingDomainSource source = new(_source);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0;
        source.AfterResponse = async (url, _) =>
        {
            if (url.AbsolutePath.EndsWith("epicgames.txt") && Interlocked.Increment(ref held) == 1)
                await gate.Task; // the first download has read the OLD content of every file and now stalls on the last one
        };
        ManagerWith(source).Apply(Config());
        Assert.True(await WaitUntil(() => held == 1));

        _source.Set("steam.txt", "*.new.example\n"); // the repository changes while the first download is stuck
        _manager!.Apply(Config("""{ "httpTimeoutSeconds": 31 }""")); // no list yet, so this save downloads again
        Assert.True(await PumpUntil(() => _manager.Matcher.Match("x.new.example") is not null, NoTime));

        gate.SetResult(); // the stuck download now completes with the old content
        await Task.Delay(300);

        Assert.Equal("steam", _manager.Matcher.Match("x.new.example")); // the newer list is still the active one
        Assert.Null(_manager.Matcher.Match("cdn.steamcontent.com"));
    }

    [Fact]
    public async Task Pause_StopsTheSchedule_AndDiscardsADownloadThatIsStillRunning()
    {
        InterceptingDomainSource source = new(_source);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AfterResponse = (_, _) => gate.Task;
        ManagerWith(source).Apply(Config());
        Assert.True(await WaitUntil(() => _source.IndexCalls == 1));

        _manager!.Pause();
        gate.SetResult();
        await Task.Delay(300);

        Assert.Equal(0, _manager.Matcher.Count);
        Assert.Null(_manager.ListFetchedAt);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task Pause_ThenApply_CountsAsAStart_SoRefreshOnStartupApplies()
    {
        Manager().Apply(Config());
        Assert.True(await PumpUntil(() => _source.IndexCalls == 1, NoTime));

        _manager!.Pause();
        _manager.Apply(Config());

        Assert.True(await PumpUntil(() => _source.IndexCalls == 2, NoTime));
    }

    [Fact]
    public async Task Pause_ThenApply_WithoutRefreshOnStartup_KeepsTheListAndDownloadsNothing()
    {
        Manager().Apply(Config("""{ "refreshOnStartup": false }"""));
        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, NoTime)); // no list at all: downloads anyway
        int calls = _source.TotalCalls;

        _manager!.Pause();
        _manager.Apply(Config("""{ "refreshOnStartup": false }"""));
        await Task.Delay(200);

        Assert.Equal(calls, _source.TotalCalls);
        Assert.Equal("steam", _manager.Matcher.Match("cdn.steamcontent.com"));
    }

    [Fact]
    public async Task Apply_AfterDispose_DoesNothing()
    {
        Manager().Dispose();

        _manager!.Apply(Config());
        await Task.Delay(200);

        Assert.Equal(0, _source.TotalCalls);
        Assert.Equal(0, _manager.Matcher.Count);
    }

    [Fact]
    public async Task FailedRefresh_RetriesAtTheNextCronTime_WhenThatIsSoonerThanFiveMinutes()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 1, 1, 3, 56, 0, TimeSpan.Zero));
        _source.FailAll = true;
        Manager().Apply(Config("""{ "refreshCron": "0 4 * * *" }""")); // fails at 03:56; the retry would be 04:01, the cron 04:00
        Assert.True(await WaitUntil(() => _source.IndexCalls == 1));
        await Task.Delay(150);

        _time.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(50)); // 03:59:50
        await Task.Delay(150);
        Assert.Equal(1, _source.IndexCalls);

        _time.Advance(TimeSpan.FromSeconds(11)); // 04:00:01
        Assert.True(await WaitUntil(() => _source.IndexCalls == 2));
    }

    [Fact]
    public async Task ScheduledRefresh_IsDelayedByJitter_ButNeverBeforeTheCronTime_AndNeverMoreThanTheMaximum()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 1, 1, 3, 59, 0, TimeSpan.Zero));
        _store.Save(Snapshot("*.persisted.example"));
        ManagerWith(_source, maxJitter: TimeSpan.FromMinutes(2)).Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "0 4 * * *" }"""));
        await Task.Delay(150);
        DateTimeOffset cron = new(2026, 1, 1, 4, 0, 0, TimeSpan.Zero);

        for (int second = 0; second < 200 && _source.IndexCalls == 0; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);

            if (_source.IndexCalls > 0)
                Assert.True(_time.GetUtcNow() >= cron, "refreshed before the cron time");
        }

        Assert.Equal(1, _source.IndexCalls);
        Assert.True(_time.GetUtcNow() <= cron + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(2), "jitter exceeded the maximum");
    }

    [Fact]
    public async Task Cron_IsEvaluatedInTheLocalTimeZoneOfTheApplication()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2")); // 03:00 UTC is 05:00 here
        _store.Save(Snapshot("*.persisted.example"));
        Manager().Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "0 6 * * *" }""")); // 06:00 local = 04:00 UTC
        await Task.Delay(150);

        _time.Advance(TimeSpan.FromMinutes(50)); // 03:50 UTC
        await Task.Delay(150);
        Assert.Equal(0, _source.IndexCalls);

        _time.Advance(TimeSpan.FromMinutes(20)); // 04:10 UTC: due in local time, but not before 06:00 UTC
        Assert.True(await WaitUntil(() => _source.IndexCalls == 1));
    }

    [Fact]
    public async Task ManyConcurrentApplies_EndWithTheLastConfiguration_AndNoErrors()
    {
        Manager();
        Parallel.For(0, 300, i => _manager!.Apply(Config(i % 2 == 0 ? """{ "services": { "exclude": ["steam"] } }""" : """{ "services": { "exclude": ["epicgames"] } }""")));
        _manager!.Apply(Config("""{ "services": { "include": ["epicgames"] } }""")); // the final, sequential save

        Assert.True(await PumpUntil(() => _manager.Matcher.Match("x.download.epicgames.com") is not null, NoTime));
        await Task.Delay(200);

        Assert.Null(_manager.Matcher.Match("cdn.steamcontent.com"));
        Assert.Equal("epicgames", _manager.Matcher.Match("x.download.epicgames.com"));
        Assert.DoesNotContain(_logs, l => l.Contains("unexpected") || l.Contains("stopped"));
    }

    /// <summary>A clock whose <see cref="GetUtcNow"/> throws a given number of times, to provoke an error in the scheduling code itself.</summary>
    private sealed class FlakyTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public int FailuresLeft;

        public override DateTimeOffset GetUtcNow()
            => Interlocked.Decrement(ref FailuresLeft) >= 0 ? throw new InvalidOperationException("clock hiccup") : base.GetUtcNow();
    }

    [Fact]
    public async Task UnexpectedErrorInTheScheduleLoop_IsLogged_AndTheLoopCarriesOn()
    {
        FlakyTimeProvider clock = new(new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero)) { FailuresLeft = 1 };
        _store.Save(Snapshot("*.persisted.example"));
        _manager = new DomainListManager(_source, _store, _logs.Enqueue, clock, TimeSpan.Zero);

        _manager.Apply(Config("""{ "refreshOnStartup": false, "refreshCron": "0 4 * * *" }"""));

        Assert.True(await WaitUntil(() => _logs.Any(l => l.Contains("unexpected error in the refresh loop") && l.Contains("clock hiccup"))));
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromMinutes(5)); // the pause after the error; 03:05
        await Task.Delay(100);
        Assert.Equal(0, _source.IndexCalls);

        clock.Advance(TimeSpan.FromMinutes(56)); // 04:01
        Assert.True(await WaitUntil(() => _source.IndexCalls == 1)); // the scheduled refresh still happens
    }

    [Fact]
    public async Task DamagedSnapshot_DoesNotStopTheManagerFromStarting()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "snapshot.json"),
            """{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://example.test/cache-domains/master/", "services": [ null ] }""");

        Manager().Apply(Config()); // must not throw

        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, NoTime)); // and downloads, since there is no usable list
    }

    [Fact]
    public async Task FailedRefresh_ThenACronThatNeverFires_StillRetriesUntilTheListArrives()
    {
        _source.FailAll = true;
        Manager().Apply(Config("""{ "refreshCron": "0 0 31 2 *" }"""));
        Assert.True(await WaitUntil(() => _source.IndexCalls == 1));
        await Task.Delay(100);

        _source.FailAll = false;
        Assert.True(await PumpUntil(() => _manager!.Matcher.Count > 0, TimeSpan.FromSeconds(30), maxSteps: 30)); // only the retry timer exists
        Assert.True(await WaitUntil(() => _logs.Any(l => l.Contains("no upcoming"))));
    }

    [Fact]
    public async Task ACronExpressionThatWasNeverValidated_IsLogged_NotThrown()
    {
        // The configuration parser rejects this; the manager must still not take the app down if it is ever given one.
        Manager().Apply(new DomainListConfig { BaseUrl = FakeDomainSource.BaseUrl, RefreshCron = "not a cron" });

        Assert.True(await WaitUntil(() => _logs.Any(l => l.Contains("refresh loop stopped unexpectedly"))));

        _manager!.Apply(Config()); // and a valid configuration afterwards works again
        Assert.True(await PumpUntil(() => _manager.Matcher.Count > 0, NoTime));
    }
}
