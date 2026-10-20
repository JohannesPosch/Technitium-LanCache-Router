using System.Collections.Concurrent;
using System.Net;
using LanCacheRouter.Config;
using LanCacheRouter.Dns;
using LanCacheRouter.DomainList;
using LanCacheRouter.Health;
using LanCacheRouter.Tests.DomainList;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace LanCacheRouter.Tests.Dns;

public class QueryHandlerTests
{
    private static readonly ResponseContext Context = new("dns01.example.test", "hostmaster@example.test", 1232);
    private static readonly IPAddress Primary = IPAddress.Parse("192.168.17.44");
    private static readonly IPAddress Secondary = IPAddress.Parse("192.168.18.44");

    private readonly ConcurrentQueue<string> _logs = new();
    private HealthSnapshot _health = Snapshot(HealthState.Healthy, HealthState.Healthy);
    private DomainMatcher _matcher = Matcher("*.steamcontent.com", "lancache.steamcontent.com");

    private static HealthSnapshot Snapshot(HealthState primary, HealthState secondary) => new(
    [
        new ServerHealth("primary", primary, Primary),
        new ServerHealth("secondary", secondary, Secondary)
    ]);

    private static DomainMatcher Matcher(params string[] steamPatterns)
        => DomainMatcher.Create(
            new RawDomainList(DateTimeOffset.UnixEpoch, FakeDomainSource.BaseUrl, [new ServiceDomains("steam", steamPatterns)]),
            new DomainListConfig(),
            _ => { });

    private static RouterConfig Config(string extra = "", string routes = """
        { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary", "failover": { "action": "failover", "server": "secondary" } }
        """)
    {
        ConfigParseResult result = ConfigParser.Parse($$"""
            {
              "cacheServers": [ { "name": "primary", "address": "192.168.17.44" }, { "name": "secondary", "address": "192.168.18.44" } ],
              "routes": [ {{routes}} ]
              {{extra}}
            }
            """);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.Config!;
    }

    private QueryHandler Handler(RouterConfig? config = null, bool configure = true)
    {
        QueryHandler handler = new(() => _health, () => _matcher, _logs.Enqueue);
        if (configure)
            handler.Configure(config ?? Config(), Context);

        return handler;
    }

    private static IPAddress AnswerAddress(DnsDatagram response) => ((DnsARecordData)response.Answer.Single().RDATA).Address;

    [Fact]
    public void CacheClient_AskingForACacheDomain_GetsTheCacheAddress()
    {
        DnsDatagram? response = Handler().Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50"));

        Assert.NotNull(response);
        Assert.Equal(Primary, AnswerAddress(response));
    }

    [Fact]
    public void NotConfigured_AnswersNothing()
    {
        Assert.Null(Handler(configure: false).Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public void Disabled_AnswersNothing()
    {
        QueryHandler handler = Handler(Config(", \"enabled\": false"));

        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public void Reconfigure_TakesEffectImmediately()
    {
        QueryHandler handler = Handler();
        DnsDatagram query = TestDns.Query("cdn1.steamcontent.com");
        IPEndPoint client = TestDns.Client("192.168.17.50");
        Assert.NotNull(handler.Handle(query, client));

        handler.Configure(Config(", \"enabled\": false"), Context);
        Assert.Null(handler.Handle(query, client));

        handler.Configure(Config(), Context);
        Assert.NotNull(handler.Handle(query, client));
    }

    [Fact]
    public void ClientOutsideAnyRoute_IsPassedThrough()
    {
        Assert.Null(Handler().Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("10.9.9.9")));
    }

    [Fact]
    public void NameThatIsNotACacheDomain_IsPassedThrough()
    {
        Assert.Null(Handler().Handle(TestDns.Query("www.example.com"), TestDns.Client("192.168.17.50")));
        Assert.Null(Handler().Handle(TestDns.Query("steamcontent.com"), TestDns.Client("192.168.17.50"))); // apex of the wildcard
    }

    [Fact]
    public void ExactEntry_AndMixedCase_Match()
    {
        QueryHandler handler = Handler();

        Assert.NotNull(handler.Handle(TestDns.Query("lancache.steamcontent.com"), TestDns.Client("192.168.17.50")));
        Assert.NotNull(handler.Handle(TestDns.Query("CDN1.SteamContent.COM"), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public void UnhealthyPrimary_FailsOverToTheSecondary()
    {
        _health = Snapshot(HealthState.Unhealthy, HealthState.Healthy);

        DnsDatagram? response = Handler().Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50"));

        Assert.Equal(Secondary, AnswerAddress(response!));
    }

    [Theory]
    [InlineData(HealthState.Unknown)]
    [InlineData(HealthState.Unhealthy)]
    [InlineData(HealthState.Disabled)]
    public void NoUsableServer_IsPassedThrough(HealthState state)
    {
        _health = Snapshot(state, state);

        Assert.Null(Handler().Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public void ChangesOfHealthAndDomainList_AreSeenWithoutReconfiguring()
    {
        QueryHandler handler = Handler();
        DnsDatagram query = TestDns.Query("cdn1.steamcontent.com");
        IPEndPoint client = TestDns.Client("192.168.17.50");

        _health = Snapshot(HealthState.Unhealthy, HealthState.Unhealthy);
        Assert.Null(handler.Handle(query, client));

        _health = Snapshot(HealthState.Healthy, HealthState.Healthy);
        Assert.NotNull(handler.Handle(query, client));

        _matcher = DomainMatcher.Empty;
        Assert.Null(handler.Handle(query, client));
    }

    [Fact]
    public void Aaaa_ForACacheDomain_IsNodata_ButOnlyForRoutedClients()
    {
        QueryHandler handler = Handler();

        DnsDatagram? routed = handler.Handle(TestDns.Query("cdn1.steamcontent.com", DnsResourceRecordType.AAAA), TestDns.Client("192.168.17.50"));
        DnsDatagram? other = handler.Handle(TestDns.Query("cdn1.steamcontent.com", DnsResourceRecordType.AAAA), TestDns.Client("10.9.9.9"));

        Assert.NotNull(routed);
        Assert.Empty(routed.Answer);
        Assert.Single(routed.Authority);
        Assert.Null(other);
    }

    [Theory]
    [InlineData(DnsResourceRecordType.TXT)]
    [InlineData(DnsResourceRecordType.MX)]
    [InlineData(DnsResourceRecordType.NS)]
    public void OtherQueryTypes_ArePassedThrough(DnsResourceRecordType type)
    {
        Assert.Null(Handler().Handle(TestDns.Query("cdn1.steamcontent.com", type), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public void NonInternetClass_IsPassedThrough()
    {
        Assert.Null(Handler().Handle(TestDns.Query("cdn1.steamcontent.com", @class: DnsClass.CH), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public void Ipv4MappedClientAddress_MatchesTheIpv4Route()
    {
        IPEndPoint mapped = new(IPAddress.Parse("::ffff:192.168.17.50"), 53000);

        Assert.NotNull(Handler().Handle(TestDns.Query("cdn1.steamcontent.com"), mapped));
    }

    [Fact]
    public void PassthroughRoute_IsPassedThrough()
    {
        QueryHandler handler = Handler(Config(routes: """{ "priority": 1, "sourceCidr": "192.168.17.0/24", "behavior": "passthrough" }"""));

        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public void QueryLogging_IsOffByDefault_AndDescribesTheAnswerWhenOn()
    {
        Handler().Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50"));
        Assert.Empty(_logs);

        QueryHandler logging = Handler(Config(", \"logging\": { \"queries\": true }"));
        logging.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50"));
        _health = Snapshot(HealthState.Unhealthy, HealthState.Healthy);
        logging.Handle(TestDns.Query("cdn1.steamcontent.com", DnsResourceRecordType.AAAA), TestDns.Client("192.168.17.50"));

        string[] lines = [.. _logs];
        Assert.Equal(2, lines.Length);
        Assert.Equal("Answered 192.168.17.50 A cdn1.steamcontent.com [steam] -> primary (192.168.17.44)", lines[0]);
        Assert.Equal("Answered 192.168.17.50 AAAA cdn1.steamcontent.com [steam] -> secondary (192.168.18.44) (failover) (no data)", lines[1]);
    }

    [Fact]
    public void VerboseLogging_ExplainsWhyQueriesArePassedThrough()
    {
        QueryHandler verbose = Handler(Config(", \"logging\": { \"verbose\": true }"));

        verbose.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("10.9.9.9"));
        verbose.Handle(TestDns.Query("example.com"), TestDns.Client("192.168.17.50"));
        _health = Snapshot(HealthState.Unhealthy, HealthState.Unhealthy);
        verbose.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50"));

        string[] lines = [.. _logs];
        Assert.Equal(3, lines.Length);
        Assert.Contains("client matches no route", lines[0]);
        Assert.Contains("not a cache domain", lines[1]);
        Assert.Contains("not Healthy", lines[2]);
    }

    [Fact]
    public void UnexpectedError_IsLoggedAndTheQueryPassedThrough()
    {
        QueryHandler handler = new(() => throw new InvalidOperationException("boom"), () => _matcher, _logs.Enqueue);
        handler.Configure(Config(), Context);

        DnsDatagram? response = handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50"));

        Assert.Null(response);
        Assert.Contains(_logs, l => l.Contains("passing it through") && l.Contains("boom"));
    }

    [Fact]
    public void MessagesWithoutExactlyOneQuestion_ArePassedThrough()
    {
        QueryHandler handler = Handler();
        IPEndPoint client = TestDns.Client("192.168.17.50");
        DnsDatagram none = new(1, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, []);
        DnsDatagram two = new(1, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            [new DnsQuestionRecord("cdn1.steamcontent.com", DnsResourceRecordType.A, DnsClass.IN), new DnsQuestionRecord("cdn2.steamcontent.com", DnsResourceRecordType.A, DnsClass.IN)]);

        Assert.Null(handler.Handle(none, client));
        Assert.Null(handler.Handle(two, client));
        Assert.Empty(_logs);
    }

    [Fact]
    public void Ipv6Client_OnAnIpv6Route_GetsTheIpv4CacheAddress_AndNodataForAaaa()
    {
        QueryHandler handler = Handler(Config(routes: """{ "priority": 1, "sourceCidr": "fd00:17::/64", "server": "primary" }"""));
        IPEndPoint client = TestDns.Client("fd00:17::50");

        DnsDatagram a = handler.Handle(TestDns.Query("cdn1.steamcontent.com"), client)!;
        DnsDatagram aaaa = handler.Handle(TestDns.Query("cdn1.steamcontent.com", DnsResourceRecordType.AAAA), client)!;

        Assert.Equal(Primary, AnswerAddress(a));
        Assert.Empty(aaaa.Answer);
        Assert.Single(aaaa.Authority);
        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("fd00:99::50"))); // another IPv6 network
        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50"))); // IPv4 client, IPv6-only route
    }

    [Theory]
    [InlineData(DnsResourceRecordType.HTTPS)]
    [InlineData(DnsResourceRecordType.SVCB)]
    public void HttpsAndSvcb_ForACacheDomain_AreNodata_ButOnlyForRoutedClients(DnsResourceRecordType type)
    {
        QueryHandler handler = Handler();

        DnsDatagram response = handler.Handle(TestDns.Query("cdn1.steamcontent.com", type), TestDns.Client("192.168.17.50"))!;

        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.Empty(response.Answer);
        Assert.Single(response.Authority);
        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com", type), TestDns.Client("10.9.9.9")));
        Assert.Null(handler.Handle(TestDns.Query("www.example.com", type), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public void PassthroughModes_LeaveAaaaAndHttpsToTechnitium_WhileAStillGetsTheCache()
    {
        QueryHandler handler = Handler(Config(", \"dns\": { \"aaaaMode\": \"passthrough\", \"httpsMode\": \"passthrough\" }"));
        IPEndPoint client = TestDns.Client("192.168.17.50");

        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com", DnsResourceRecordType.AAAA), client));
        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com", DnsResourceRecordType.HTTPS), client));
        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com", DnsResourceRecordType.SVCB), client));
        Assert.NotNull(handler.Handle(TestDns.Query("cdn1.steamcontent.com"), client));
        Assert.Empty(_logs);
    }

    [Fact]
    public void ReconfiguredTtl_AppliesToTheNextAnswer()
    {
        QueryHandler handler = Handler();
        DnsDatagram query = TestDns.Query("cdn1.steamcontent.com");
        IPEndPoint client = TestDns.Client("192.168.17.50");
        Assert.Equal(60u, handler.Handle(query, client)!.Answer[0].TTL);

        handler.Configure(Config(", \"dns\": { \"ttl\": 7 }"), Context);

        Assert.Equal(7u, handler.Handle(query, client)!.Answer[0].TTL);
    }

    public static TheoryData<string> OddNames() => new()
    {
        "", // the root
        "com",
        string.Join('.', Enumerable.Repeat("a", 100)) + ".steamcontent.com", // 102 labels
        new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + ".steamcontent.com", // near the 253 character limit
        "_dmarc._tcp.steamcontent.com",
        "xn--bcher-kva.steamcontent.com",
        "UPPER.CASE.STEAMCONTENT.COM",
    };

    [Theory]
    [MemberData(nameof(OddNames))]
    public void UnusualNames_NeverMakeTheHandlerThrow(string name)
    {
        QueryHandler handler = Handler();

        DnsDatagram? response = handler.Handle(TestDns.Query(name), TestDns.Client("192.168.17.50"));

        bool isCacheDomain = _matcher.Match(name) is not null;
        Assert.Equal(isCacheDomain, response is not null);
        Assert.DoesNotContain(_logs, l => l.Contains("Error handling query"));
        if (response is not null)
            Assert.Equal(name, response.Answer[0].Name); // the owner is the name exactly as asked
    }

    [Fact]
    public void ParallelQueries_EachGetTheirOwnIdentifierAndNameBack()
    {
        QueryHandler handler = Handler();
        IPEndPoint client = TestDns.Client("192.168.17.50");
        int mismatches = 0;

        Parallel.For(0, 5000, i =>
        {
            ushort id = (ushort)i;
            string name = $"host{i % 50}.steamcontent.com";
            DnsDatagram? response = handler.Handle(TestDns.Query(name, id: id), client);

            if (response is null || response.Identifier != id || response.Answer[0].Name != name || !AnswerAddress(response).Equals(Primary))
                Interlocked.Increment(ref mismatches);
        });

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void ALoggerThatThrows_NeverTurnsAnAnswerIntoAnError()
    {
        QueryHandler handler = new(() => _health, () => _matcher, _ => throw new IOException("log disk full"));
        handler.Configure(Config(", \"logging\": { \"queries\": true }"), Context);

        DnsDatagram? answered = handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50"));

        Assert.NotNull(answered);
        Assert.Equal(Primary, AnswerAddress(answered));
    }

    [Fact]
    public void ALoggerThatThrows_StillLetsAnInternalFailurePassThrough()
    {
        QueryHandler handler = new(() => throw new InvalidOperationException("boom"), () => _matcher, _ => throw new IOException("log disk full"));
        handler.Configure(Config(), Context);

        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public void MatcherFailure_IsPassedThroughAndLogged()
    {
        QueryHandler handler = new(() => _health, () => throw new InvalidOperationException("matcher exploded"), _logs.Enqueue);
        handler.Configure(Config(), Context);

        Assert.Null(handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.50")));
        Assert.Contains(_logs, l => l.Contains("matcher exploded"));
    }

    [Fact]
    public void RoutedClientsOfDifferentRoutes_GetTheirOwnServers_InTheSameInstant()
    {
        QueryHandler handler = Handler(Config(routes: """
            { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary" },
            { "priority": 20, "sourceCidr": "192.168.18.0/24", "server": "secondary" }
            """));

        Assert.Equal(Primary, AnswerAddress(handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.17.9"))!));
        Assert.Equal(Secondary, AnswerAddress(handler.Handle(TestDns.Query("cdn1.steamcontent.com"), TestDns.Client("192.168.18.9"))!));
    }

    [Fact]
    public void Handler_IsSafeUnderConcurrentReconfiguration()
    {
        QueryHandler handler = Handler();
        DnsDatagram query = TestDns.Query("cdn1.steamcontent.com");
        IPEndPoint client = TestDns.Client("192.168.17.50");
        RouterConfig on = Config(), off = Config(", \"enabled\": false");

        Parallel.For(0, 2000, i =>
        {
            if (i % 10 == 0)
                handler.Configure(i % 20 == 0 ? on : off, Context);
            else
                handler.Handle(query, client); // must neither throw nor return a malformed answer
        });

        Assert.DoesNotContain(_logs, l => l.Contains("Error handling query"));
    }
}
