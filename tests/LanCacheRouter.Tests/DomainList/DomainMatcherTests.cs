using LanCacheRouter.Config;
using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests.DomainList;

public class DomainMatcherTests
{
    private static RawDomainList List(params (string Service, string[] Patterns)[] services)
        => new(DateTimeOffset.UnixEpoch, FakeDomainSource.BaseUrl, [.. services.Select(s => new ServiceDomains(s.Service, s.Patterns))]);

    private static DomainListConfig Config(string json = "{}")
    {
        ConfigParseResult result = ConfigParser.Parse($$"""{ "domainList": {{json}} }""");
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.Config!.DomainList;
    }

    private static DomainMatcher Create(RawDomainList list, DomainListConfig? config = null, List<string>? log = null)
        => DomainMatcher.Create(list, config ?? Config(), log is null ? _ => { } : log.Add);

    [Fact]
    public void Empty_MatchesNothing()
    {
        Assert.Null(DomainMatcher.Empty.Match("steamcontent.com"));
        Assert.Equal(0, DomainMatcher.Empty.Count);
    }

    [Fact]
    public void Exact_MatchesOnlyThatName()
    {
        DomainMatcher matcher = Create(List(("steam", ["lancache.steamcontent.com"])));

        Assert.Equal("steam", matcher.Match("lancache.steamcontent.com"));
        Assert.Null(matcher.Match("steamcontent.com"));
        Assert.Null(matcher.Match("a.lancache.steamcontent.com"));
        Assert.Null(matcher.Match("other.steamcontent.com"));
    }

    [Theory]
    [InlineData("cdn.steamcontent.com", true)]
    [InlineData("a.b.c.steamcontent.com", true)]
    [InlineData("steamcontent.com", false)] // the apex is not covered by *.steamcontent.com
    [InlineData("notsteamcontent.com", false)]
    [InlineData("steamcontent.com.evil.net", false)]
    [InlineData("com", false)]
    [InlineData("", false)]
    public void Wildcard_CoversSubdomainsAtAnyDepth_ButNotTheApex(string name, bool expected)
    {
        DomainMatcher matcher = Create(List(("steam", ["*.steamcontent.com"])));

        Assert.Equal(expected, matcher.Match(name) is not null);
    }

    [Theory]
    [InlineData("CDN.SteamContent.COM")]
    [InlineData("cdn.steamcontent.com.")]
    [InlineData("Cdn.SteamContent.Com.")]
    public void Matching_IgnoresCaseAndTrailingDot(string name)
    {
        Assert.Equal("steam", Create(List(("steam", ["*.steamcontent.com"]))).Match(name));
    }

    [Fact]
    public void ExactEntry_BeatsWildcard_AndDeeperWildcardBeatsShallower()
    {
        DomainMatcher matcher = Create(List(
            ("wide", ["*.example.com"]),
            ("exact", ["www.example.com"]),
            ("deep", ["*.cdn.example.com"])));

        Assert.Equal("exact", matcher.Match("www.example.com"));
        Assert.Equal("deep", matcher.Match("a.cdn.example.com"));
        Assert.Equal("wide", matcher.Match("other.example.com"));
        Assert.Equal("wide", matcher.Match("cdn.example.com")); // apex of the deep wildcard falls back to the wider one
    }

    [Fact]
    public void WildcardAndExactOfTheSameBase_BothWork()
    {
        DomainMatcher matcher = Create(List(("s", ["example.com", "*.example.com"])));

        Assert.Equal("s", matcher.Match("example.com"));
        Assert.Equal("s", matcher.Match("a.example.com"));
    }

    [Fact]
    public void SamePatternInTwoServices_FirstServiceKeepsIt()
    {
        DomainMatcher matcher = Create(List(("first", ["*.shared.com"]), ("second", ["*.shared.com"])));

        Assert.Equal("first", matcher.Match("a.shared.com"));
        Assert.Equal(1, matcher.Count);
    }

    [Fact]
    public void ServiceInclude_LimitsToTheListedServices()
    {
        RawDomainList list = List(("steam", ["*.a.com"]), ("epic", ["*.b.com"]), ("blizzard", ["*.c.com"]));
        DomainMatcher matcher = Create(list, Config("""{ "services": { "include": ["Steam", "epic"] } }"""));

        Assert.Equal("steam", matcher.Match("x.a.com"));
        Assert.Equal("epic", matcher.Match("x.b.com"));
        Assert.Null(matcher.Match("x.c.com"));
    }

    [Fact]
    public void ServiceExclude_DropsServices()
    {
        RawDomainList list = List(("steam", ["*.a.com"]), ("epic", ["*.b.com"]));
        DomainMatcher matcher = Create(list, Config("""{ "services": { "exclude": ["epic"] } }"""));

        Assert.Equal("steam", matcher.Match("x.a.com"));
        Assert.Null(matcher.Match("x.b.com"));
    }

    [Fact]
    public void UnknownServiceInFilter_IsLoggedAsWarning()
    {
        List<string> log = [];

        Create(List(("steam", ["*.a.com"])), Config("""{ "services": { "include": ["steam", "stem"] } }"""), log);

        Assert.Contains(log, l => l.Contains("'stem'"));
        Assert.DoesNotContain(log, l => l.Contains("'steam'"));
    }

    [Fact]
    public void ExtraDomains_AreAdded_EvenIfTheirServiceIsNotInTheInclude()
    {
        DomainMatcher matcher = Create(
            List(("steam", ["*.a.com"])),
            Config("""{ "services": { "include": ["steam"] }, "extraDomains": [ { "domain": "*.mycdn.net", "service": "custom" }, { "domain": "update.vendor.org" } ] }"""));

        Assert.Equal("custom", matcher.Match("x.mycdn.net"));
        Assert.Equal("custom", matcher.Match("update.vendor.org"));
        Assert.Equal(3, matcher.Count);
    }

    [Fact]
    public void ExcludeDomains_CarveOutOfWildcards()
    {
        DomainMatcher matcher = Create(
            List(("steam", ["*.steamcontent.com"])),
            Config("""{ "excludeDomains": ["login.steamcontent.com", "*.api.steamcontent.com"] }"""));

        Assert.Equal("steam", matcher.Match("cdn.steamcontent.com"));
        Assert.Null(matcher.Match("login.steamcontent.com"));
        Assert.Null(matcher.Match("v1.api.steamcontent.com"));
        Assert.Equal("steam", matcher.Match("api.steamcontent.com")); // apex of the excluded wildcard is not excluded
    }

    [Fact]
    public void ServiceCounts_CountPatternsPerService()
    {
        DomainMatcher matcher = Create(List(("steam", ["*.a.com", "b.com"]), ("epic", ["*.c.com"])));

        Assert.Equal(3, matcher.Count);
        Assert.Equal(2, matcher.ServiceCounts["steam"]);
        Assert.Equal(1, matcher.ServiceCounts["epic"]);
    }

    [Fact]
    public void InvalidPatternsInTheList_AreSkipped()
    {
        DomainMatcher matcher = Create(List(("s", ["*.ok.com", "*", "com", "bad domain"])));

        Assert.Equal(1, matcher.Count);
    }

    [Fact]
    public void ExcludeDomains_WinOverEverything_IncludingExactEntriesAndExtraDomains()
    {
        DomainMatcher matcher = Create(
            List(("steam", ["exact.example.com", "*.example.com"])),
            Config("""{ "extraDomains": [ { "domain": "extra.example.org", "service": "mine" } ], "excludeDomains": ["exact.example.com", "extra.example.org", "*.blocked.example.com"] }"""));

        Assert.Null(matcher.Match("exact.example.com"));
        Assert.Null(matcher.Match("extra.example.org"));
        Assert.Equal("steam", matcher.Match("other.example.com"));
    }

    [Fact]
    public void ExcludeDomains_ThatAreNotValid_AreIgnored_NotFatal()
    {
        // The validator rejects these, but the matcher must not depend on that to stay safe.
        DomainListConfig config = new() { ExcludeDomains = ["", "   ", "*", "com", "bad domain", "ok.example.com"], ExtraDomains = [new ExtraDomainConfig { Domain = "also bad", Service = "x" }] };

        DomainMatcher matcher = Create(List(("s", ["*.example.com"])), config);

        Assert.Null(matcher.Match("ok.example.com"));
        Assert.Equal("s", matcher.Match("other.example.com"));
        Assert.Equal(1, matcher.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a..b.com")]
    [InlineData(".a.com")]
    [InlineData("   ")]
    [InlineData("a b.com")]
    [InlineData("*.steamcontent.com")]
    [InlineData("a.*.steamcontent.com")]
    [InlineData("ünïcode.steamcontent.com")]
    [InlineData("\0.steamcontent.com")]
    public void Match_NeverThrows_ForMalformedQueryNames(string name)
    {
        DomainMatcher matcher = Create(List(("steam", ["*.steamcontent.com"])));

        string? result = matcher.Match(name);

        Assert.True(result is null or "steam");
    }

    [Fact]
    public void Match_HandlesVeryDeepAndVeryLongNames()
    {
        DomainMatcher matcher = Create(List(("steam", ["*.steamcontent.com"])));
        string deep = string.Join('.', Enumerable.Repeat("a", 5000)) + ".steamcontent.com";
        string longLabel = new string('a', 100000) + ".steamcontent.com";

        Assert.Equal("steam", matcher.Match(deep));
        Assert.Equal("steam", matcher.Match(longLabel));
    }

    [Fact]
    public void DeepPatterns_AreMatchedExactlyAtTheirDepth()
    {
        DomainMatcher matcher = Create(List(("s", ["*.a.b.c.d.example.com", "x.y.example.com"])));

        Assert.Equal("s", matcher.Match("q.a.b.c.d.example.com"));
        Assert.Equal("s", matcher.Match("r.q.a.b.c.d.example.com"));
        Assert.Null(matcher.Match("a.b.c.d.example.com")); // apex of the wildcard
        Assert.Null(matcher.Match("q.b.c.d.example.com")); // one label missing
        Assert.Equal("s", matcher.Match("x.y.example.com"));
        Assert.Null(matcher.Match("y.example.com"));
        Assert.Null(matcher.Match("z.x.y.example.com")); // exact entries do not cover subdomains
    }

    [Fact]
    public void LargeLists_BuildAndMatchCorrectly()
    {
        string[] patterns = [.. Enumerable.Range(0, 60000).Select(i => i % 3 == 0 ? $"host{i}.cdn{i % 100}.example.com" : $"*.zone{i}.example.net")];
        DomainMatcher matcher = Create(List(("big", patterns)));

        Assert.Equal(60000, matcher.Count);
        for (int i = 0; i < 60000; i += 97)
        {
            string name = i % 3 == 0 ? $"host{i}.cdn{i % 100}.example.com" : $"deep.sub.zone{i}.example.net";
            Assert.Equal("big", matcher.Match(name));
        }

        Assert.Null(matcher.Match("host1.cdn1.example.com"));
        Assert.Null(matcher.Match("zone1.example.net"));
    }

    [Fact]
    public void AMatcher_CanBeReadFromManyThreadsAtOnce()
    {
        DomainMatcher matcher = Create(List(("steam", ["*.steamcontent.com", "lancache.steamcontent.com"]), ("epic", ["*.epicgames.com"])));
        int wrong = 0;

        Parallel.For(0, 50000, i =>
        {
            (string Name, string? Expected) probe = (i % 4) switch
            {
                0 => ("cdn.steamcontent.com", "steam"),
                1 => ("X.EpicGames.com.", "epic"),
                2 => ("example.org", null),
                _ => ("steamcontent.com", null),
            };

            if (matcher.Match(probe.Name) != probe.Expected)
                Interlocked.Increment(ref wrong);
        });

        Assert.Equal(0, wrong);
    }

    [Fact]
    public void ServiceNames_InFilters_AreComparedIgnoringCase_AndUnknownOnesAreReportedOnce()
    {
        List<string> log = [];

        DomainMatcher matcher = Create(List(("Steam", ["*.a.com"]), ("Epic", ["*.b.com"])), Config("""{ "services": { "include": ["STEAM", "nope", "NOPE"], "exclude": ["gone"] } }"""), log);

        Assert.Equal("Steam", matcher.Match("x.a.com"));
        Assert.Null(matcher.Match("x.b.com"));
        Assert.Single(log, l => l.Contains("'nope'", StringComparison.OrdinalIgnoreCase));
        Assert.Single(log, l => l.Contains("'gone'"));
    }

    [Fact]
    public void AnEmptyList_WithOnlyExtraDomains_Works()
    {
        DomainMatcher matcher = Create(new RawDomainList(DateTimeOffset.UnixEpoch, FakeDomainSource.BaseUrl, []), Config("""{ "extraDomains": [ { "domain": "*.mine.example" } ] }"""));

        Assert.Equal("custom", matcher.Match("a.mine.example"));
        Assert.Equal(1, matcher.Count);
    }

    [Fact]
    public void SameExactEntryInTwoServices_FirstServiceKeepsIt()
    {
        DomainMatcher matcher = Create(List(("first", ["host.shared.com"]), ("second", ["host.shared.com"])));

        Assert.Equal("first", matcher.Match("host.shared.com"));
        Assert.Equal(1, matcher.Count);
        Assert.Equal(1, matcher.ServiceCounts["first"]);
        Assert.False(matcher.ServiceCounts.ContainsKey("second"));
    }
}
