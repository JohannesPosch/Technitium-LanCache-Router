using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests.DomainList;

public class DomainListFetcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Task<RawDomainList> Fetch(FakeDomainSource source, string baseUrl = FakeDomainSource.BaseUrl, List<string>? log = null)
        => DomainListFetcher.FetchAsync(baseUrl, Timeout, source, log is null ? _ => { } : log.Add, Now, CancellationToken.None);

    [Fact]
    public async Task Fetch_ReturnsAllServicesWithTheirPatterns()
    {
        RawDomainList list = await Fetch(FakeDomainSource.WithSteamAndEpic());

        Assert.Equal(Now, list.FetchedAt);
        Assert.Equal(FakeDomainSource.BaseUrl, list.BaseUrl);
        Assert.Equal(["steam", "epicgames"], list.Services.Select(s => s.Name));
        Assert.Equal(["*.steamcontent.com", "lancache.steamcontent.com"], list.Services[0].Patterns);
        Assert.Equal(["epicgames-download1.akamaized.net", "*.download.epicgames.com"], list.Services[1].Patterns);
    }

    [Fact]
    public async Task Fetch_AddsMissingTrailingSlashToBaseUrl()
    {
        RawDomainList list = await Fetch(FakeDomainSource.WithSteamAndEpic(), FakeDomainSource.BaseUrl.TrimEnd('/'));

        Assert.Equal(2, list.Services.Count);
        Assert.Equal(FakeDomainSource.BaseUrl, list.BaseUrl);
    }

    [Fact]
    public async Task Fetch_IsAllOrNothing_WhenOneFileFails()
    {
        FakeDomainSource source = FakeDomainSource.WithSteamAndEpic();
        source.FailSuffix = "epicgames.txt";

        await Assert.ThrowsAsync<HttpRequestException>(() => Fetch(source));
    }

    [Fact]
    public async Task Fetch_Fails_WhenTheIndexIsMissing()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => Fetch(new FakeDomainSource()));
    }

    [Fact]
    public async Task Fetch_Fails_WhenTheIndexListsNoServices()
    {
        FakeDomainSource source = new FakeDomainSource().Set("cache_domains.json", """{ "cache_domains": [] }""");

        await Assert.ThrowsAsync<FormatException>(() => Fetch(source));
    }

    [Fact]
    public async Task Fetch_DownloadsAFileSharedByTwoServicesOnce()
    {
        FakeDomainSource source = new FakeDomainSource()
            .Set("cache_domains.json", """
                { "cache_domains": [
                    { "name": "a", "domain_files": ["shared.txt"] },
                    { "name": "b", "domain_files": ["shared.txt", "own.txt"] }
                ] }
                """)
            .Set("shared.txt", "*.shared.com\n")
            .Set("own.txt", "*.own.com\n");

        RawDomainList list = await Fetch(source);

        Assert.Equal(1, source.Calls("shared.txt"));
        Assert.Equal(["*.shared.com"], list.Services[0].Patterns);
        Assert.Equal(["*.shared.com", "*.own.com"], list.Services[1].Patterns);
    }

    [Fact]
    public async Task Fetch_MergesMultipleFilesOfAService_WithoutDuplicates()
    {
        FakeDomainSource source = new FakeDomainSource()
            .Set("cache_domains.json", """{ "cache_domains": [ { "name": "n", "domain_files": ["1.txt", "2.txt"] } ] }""")
            .Set("1.txt", "a.com\nb.com\n")
            .Set("2.txt", "b.com\nc.com\n");

        RawDomainList list = await Fetch(source);

        Assert.Equal(["a.com", "b.com", "c.com"], list.Services[0].Patterns);
    }

    [Fact]
    public async Task Fetch_LogsSkippedInvalidLines_ButStillSucceeds()
    {
        FakeDomainSource source = new FakeDomainSource()
            .Set("cache_domains.json", """{ "cache_domains": [ { "name": "n", "domain_files": ["1.txt"] } ] }""")
            .Set("1.txt", "ok.com\nbad domain\n");
        List<string> log = [];

        RawDomainList list = await Fetch(source, log: log);

        Assert.Equal(["ok.com"], list.Services[0].Patterns);
        Assert.Contains(log, l => l.Contains("skipped 1 invalid") && l.Contains("bad domain"));
    }

    [Theory]
    [InlineData("https://evil.example/steam.txt")] // other host
    [InlineData("http://example.test/cache-domains/master/steam.txt")] // other scheme
    [InlineData("../other/steam.txt")] // escapes the base path
    [InlineData("/steam.txt")] // root of the host, outside the base path
    [InlineData("a/../../x.txt")]
    [InlineData("//evil.example/steam.txt")] // scheme-relative URL to another host
    [InlineData("%2e%2e/other/steam.txt")] // percent-encoded parent directory
    [InlineData("..%2fother/steam.txt")]
    public async Task Fetch_RejectsFilesOutsideTheBaseUrl(string file)
    {
        FakeDomainSource source = new FakeDomainSource()
            .Set("cache_domains.json", $$"""{ "cache_domains": [ { "name": "n", "domain_files": ["{{file}}"] } ] }""");

        await Assert.ThrowsAsync<FormatException>(() => Fetch(source));
        Assert.Equal(1, source.TotalCalls); // only the index was requested
    }

    [Fact]
    public async Task Fetch_AcceptsFilesInASubfolderOfTheBaseUrl()
    {
        FakeDomainSource source = new FakeDomainSource()
            .Set("cache_domains.json", """{ "cache_domains": [ { "name": "n", "domain_files": ["lists/n.txt"] } ] }""")
            .Set("lists/n.txt", "example.com\n");

        RawDomainList list = await Fetch(source);

        Assert.Equal(["example.com"], list.Services[0].Patterns);
    }

    [Fact]
    public async Task Fetch_Stops_WhenCancelled()
    {
        InterceptingDomainSource source = new(FakeDomainSource.WithSteamAndEpic())
        {
            BeforeRequest = (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DomainListFetcher.FetchAsync(FakeDomainSource.BaseUrl, Timeout, source, _ => { }, Now, cancelled.Token));
    }

    [Fact]
    public async Task Fetch_PassesTheTimeoutOnToEveryRequest()
    {
        List<TimeSpan> seen = [];
        RecordingSource source = new(FakeDomainSource.WithSteamAndEpic(), seen);

        await DomainListFetcher.FetchAsync(FakeDomainSource.BaseUrl, TimeSpan.FromSeconds(7), source, _ => { }, Now, CancellationToken.None);

        Assert.Equal(3, seen.Count); // index + two files
        Assert.All(seen, t => Assert.Equal(TimeSpan.FromSeconds(7), t));
    }

    private sealed class RecordingSource(IDomainSource inner, List<TimeSpan> timeouts) : IDomainSource
    {
        public Task<string> GetStringAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
        {
            timeouts.Add(timeout);
            return inner.GetStringAsync(url, timeout, cancellationToken);
        }
    }

    [Fact]
    public async Task Fetch_KeepsAServiceWhoseFileIsEmpty()
    {
        FakeDomainSource source = new FakeDomainSource()
            .Set("cache_domains.json", """{ "cache_domains": [ { "name": "empty", "domain_files": ["e.txt"] }, { "name": "full", "domain_files": ["f.txt"] } ] }""")
            .Set("e.txt", "# nothing yet\n")
            .Set("f.txt", "a.com\n");

        RawDomainList list = await Fetch(source);

        Assert.Equal(["empty", "full"], list.Services.Select(s => s.Name));
        Assert.Empty(list.Services[0].Patterns);
    }

    [Fact]
    public async Task Fetch_Fails_WhenTheIndexIsNotJson()
    {
        FakeDomainSource source = new FakeDomainSource().Set("cache_domains.json", "<html>404: Not Found</html>");

        await Assert.ThrowsAsync<FormatException>(() => Fetch(source));
    }

    [Fact]
    public async Task Fetch_Fails_WhenTheIndexIsEmpty()
    {
        await Assert.ThrowsAsync<FormatException>(() => Fetch(new FakeDomainSource().Set("cache_domains.json", "")));
    }

    [Fact]
    public async Task Fetch_ConfinesEveryRequestToTheBaseUrl_ByDefault()
    {
        ConfinementRecordingSource source = new(FakeDomainSource.WithSteamAndEpic());

        await DomainListFetcher.FetchAsync(FakeDomainSource.BaseUrl, Timeout, source, _ => { }, Now, CancellationToken.None);

        Assert.Equal(3, source.Confinements.Count); // index + two files
        Assert.All(source.Confinements, c => Assert.Equal(new Uri(FakeDomainSource.BaseUrl), c));
    }

    [Fact]
    public async Task Fetch_DoesNotConfineRequests_WhenTheRedirectCheckIsOff()
    {
        ConfinementRecordingSource source = new(FakeDomainSource.WithSteamAndEpic());

        await DomainListFetcher.FetchAsync(FakeDomainSource.BaseUrl, Timeout, source, _ => { }, Now, CancellationToken.None, restrictRedirects: false);

        Assert.Equal(3, source.Confinements.Count);
        Assert.All(source.Confinements, Assert.Null);
    }

    [Fact]
    public async Task Fetch_Fails_WhenTheSourceRejectsARedirectedResponse()
    {
        FakeDomainSource inner = FakeDomainSource.WithSteamAndEpic();
        InterceptingDomainSource source = new(inner)
        {
            BeforeRequest = (url, _) => url.AbsolutePath.EndsWith("steam.txt")
                ? throw new HttpRequestException("redirected outside the base URL")
                : Task.CompletedTask
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => Fetch(source));
    }

    private static Task<RawDomainList> Fetch(IDomainSource source, DomainListLimits? limits = null)
        => DomainListFetcher.FetchAsync(FakeDomainSource.BaseUrl, Timeout, source, _ => { }, Now, CancellationToken.None, limits: limits);

    private static FakeDomainSource Files(int count, string content = "a.example.com\n")
    {
        FakeDomainSource source = new();
        source.Set("cache_domains.json", "{ \"cache_domains\": [" + string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{ "name": "s{{i}}", "domain_files": ["f{{i}}.txt"] }""")) + "] }");
        for (int i = 0; i < count; i++)
            source.Set($"f{i}.txt", content);

        return source;
    }

    [Fact]
    public async Task Limits_TheRealListIsFarBelowTheDefaults()
    {
        Assert.Equal(26, (await Fetch(Files(26))).Services.Count);
    }

    [Fact]
    public async Task Limits_NumberOfFiles_ExactlyAtTheLimitIsAccepted_OneMoreFails()
    {
        DomainListLimits limits = DomainListLimits.Default with { MaxFiles = 5 };

        Assert.Equal(5, (await Fetch(Files(5), limits)).Services.Count);

        FormatException error = await Assert.ThrowsAsync<FormatException>(() => Fetch(Files(6), limits));
        Assert.Contains("5 files", error.Message);
    }

    [Fact]
    public async Task Limits_AFileListedByManyServices_CountsOnce()
    {
        FakeDomainSource source = new();
        source.Set("cache_domains.json", "{ \"cache_domains\": [" + string.Join(",", Enumerable.Range(0, 20).Select(i => $$"""{ "name": "s{{i}}", "domain_files": ["shared.txt"] }""")) + "] }");
        source.Set("shared.txt", "a.example.com\n");

        Assert.Equal(20, (await Fetch(source, DomainListLimits.Default with { MaxFiles = 1 })).Services.Count);
    }

    [Fact]
    public async Task Limits_TotalSize_IsCheckedAcrossTheIndexAndAllFiles()
    {
        FakeDomainSource source = Files(10, content: "a.example.com\n" + new string('#', 1000)); // about 1 kB per file
        DomainListLimits limits = DomainListLimits.Default with { MaxTotalCharacters = 5000 };

        FormatException error = await Assert.ThrowsAsync<FormatException>(() => Fetch(source, limits));
        Assert.Contains("5000 characters", error.Message);

        Assert.Equal(10, (await Fetch(source, limits with { MaxTotalCharacters = 50_000 })).Services.Count);
    }

    [Fact]
    public async Task Limits_NumberOfPatterns_ExactlyAtTheLimitIsAccepted_OneMoreFails()
    {
        FakeDomainSource source = Files(1, content: string.Join('\n', Enumerable.Range(0, 10).Select(i => $"h{i}.example.com")));

        Assert.Single(await Task.FromResult((await Fetch(source, DomainListLimits.Default with { MaxPatterns = 10 })).Services));

        FormatException error = await Assert.ThrowsAsync<FormatException>(() => Fetch(source, DomainListLimits.Default with { MaxPatterns = 9 }));
        Assert.Contains("9 patterns", error.Message);
    }

    [Fact]
    public async Task Limits_NumberOfPatterns_CountsEveryServiceThatListsTheSameFile()
    {
        // One big file referenced by many services would otherwise multiply in memory: 50 services x 10 patterns.
        FakeDomainSource source = new();
        source.Set("cache_domains.json", "{ \"cache_domains\": [" + string.Join(",", Enumerable.Range(0, 50).Select(i => $$"""{ "name": "s{{i}}", "domain_files": ["shared.txt"] }""")) + "] }");
        source.Set("shared.txt", string.Join('\n', Enumerable.Range(0, 10).Select(i => $"h{i}.example.com")));

        await Assert.ThrowsAsync<FormatException>(() => Fetch(source, DomainListLimits.Default with { MaxPatterns = 400 }));
        Assert.Equal(50, (await Fetch(source, DomainListLimits.Default with { MaxPatterns = 500 })).Services.Count);
    }

    [Fact]
    public async Task Fetch_LogsUntrustedLinesAndFileNamesEscaped()
    {
        FakeDomainSource source = new FakeDomainSource()
            .Set("cache_domains.json", """{ "cache_domains": [ { "name": "n", "domain_files": ["1.txt"] } ] }""")
            .Set("1.txt", "ok.com\nbad\rLanCache Router: forged\n");
        List<string> log = [];

        await Fetch(source, log: log);

        string line = Assert.Single(log);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("bad\\x0DLanCache Router: forged", line);
    }

    [Fact]
    public async Task Fetch_RejectionOfAHostileFileName_CannotCarryLineBreaks()
    {
        FakeDomainSource source = new FakeDomainSource()
            .Set("cache_domains.json", """{ "cache_domains": [ { "name": "n", "domain_files": ["evil\nLanCache Router: forged/../x.txt"] } ] }""");

        FormatException error = await Assert.ThrowsAsync<FormatException>(() => Fetch(source));

        Assert.DoesNotContain('\n', error.Message);
    }

    [Fact]
    public async Task Fetch_HandlesHundredsOfServices()
    {
        FakeDomainSource source = new();
        source.Set("cache_domains.json", "{ \"cache_domains\": [" + string.Join(",", Enumerable.Range(0, 400).Select(i => $$"""{ "name": "s{{i}}", "domain_files": ["f{{i}}.txt"] }""")) + "] }");
        for (int i = 0; i < 400; i++)
            source.Set($"f{i}.txt", $"*.s{i}.example.com\nhost.s{i}.example.com\n");

        RawDomainList list = await Fetch(source);

        Assert.Equal(400, list.Services.Count);
        Assert.All(list.Services, s => Assert.Equal(2, s.Patterns.Count));
    }
}
