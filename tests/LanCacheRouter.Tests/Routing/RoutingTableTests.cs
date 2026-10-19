using System.Net;
using LanCacheRouter.Config;
using LanCacheRouter.Health;
using LanCacheRouter.Routing;

namespace LanCacheRouter.Tests.Routing;

public class RoutingTableTests
{
    private static readonly IPAddress Client17 = IPAddress.Parse("192.168.17.50");
    private static readonly IPAddress Client18 = IPAddress.Parse("192.168.18.50");
    private static readonly IPAddress Other = IPAddress.Parse("10.9.9.9");

    private static RoutingTable Table(string routes)
    {
        string json = $$"""
            {
              "cacheServers": [
                { "name": "primary",   "address": "192.168.17.44" },
                { "name": "secondary", "address": "192.168.18.44" }
              ],
              "routes": [ {{routes}} ]
            }
            """;
        ConfigParseResult result = ConfigParser.Parse(json);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return RoutingTable.Create(result.Config!);
    }

    private static HealthSnapshot Health(HealthState primary, HealthState secondary = HealthState.Healthy) => new(
    [
        new ServerHealth("primary", primary, IPAddress.Parse("192.168.17.44")),
        new ServerHealth("secondary", secondary, IPAddress.Parse("192.168.18.44"))
    ]);

    private const string Row17 = """{ "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary" }""";

    private const string Row17WithFailover = """
        { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary",
          "failover": { "action": "failover", "server": "secondary" } }
        """;

    [Fact]
    public void ClientWithoutMatchingRoute_IsPassedThrough()
    {
        RoutingDecision decision = Table(Row17).Resolve(Other, Health(HealthState.Healthy));

        Assert.Equal(RoutingOutcome.Passthrough, decision.Outcome);
        Assert.Null(decision.Route);
        Assert.Null(decision.Server);
    }

    [Fact]
    public void EmptyTable_PassesEverythingThrough()
    {
        Assert.Equal(RoutingOutcome.Passthrough, RoutingTable.Empty.Resolve(Client17, Health(HealthState.Healthy)).Outcome);
    }

    [Fact]
    public void HealthyPrimary_IsUsed()
    {
        RoutingDecision decision = Table(Row17).Resolve(Client17, Health(HealthState.Healthy));

        Assert.Equal(RoutingOutcome.Cache, decision.Outcome);
        Assert.Equal("primary", decision.Server!.Name);
        Assert.Equal(IPAddress.Parse("192.168.17.44"), decision.Server.Address);
        Assert.False(decision.FailedOver);
        Assert.Equal(10, decision.Route!.Priority);
    }

    [Theory]
    [InlineData(HealthState.Unknown)]
    [InlineData(HealthState.Unhealthy)]
    [InlineData(HealthState.Disabled)]
    public void UnusablePrimary_WithPassthroughFailover_IsPassedThrough(HealthState state)
    {
        RoutingDecision decision = Table(Row17).Resolve(Client17, Health(state));

        Assert.Equal(RoutingOutcome.Passthrough, decision.Outcome);
        Assert.Equal(10, decision.Route!.Priority); // matched, but nothing usable
        Assert.Null(decision.Server);
    }

    [Theory]
    [InlineData(HealthState.Unknown)]
    [InlineData(HealthState.Unhealthy)]
    [InlineData(HealthState.Disabled)]
    public void UnusablePrimary_FailsOverToHealthySecondary(HealthState state)
    {
        RoutingDecision decision = Table(Row17WithFailover).Resolve(Client17, Health(state));

        Assert.Equal(RoutingOutcome.Cache, decision.Outcome);
        Assert.Equal("secondary", decision.Server!.Name);
        Assert.True(decision.FailedOver);
    }

    [Fact]
    public void HealthyPrimary_IsPreferredOverFailover()
    {
        RoutingDecision decision = Table(Row17WithFailover).Resolve(Client17, Health(HealthState.Healthy));

        Assert.Equal("primary", decision.Server!.Name);
        Assert.False(decision.FailedOver);
    }

    [Fact]
    public void BothServersUnusable_IsPassedThrough()
    {
        RoutingDecision decision = Table(Row17WithFailover).Resolve(Client17, Health(HealthState.Unhealthy, HealthState.Unknown));

        Assert.Equal(RoutingOutcome.Passthrough, decision.Outcome);
        Assert.False(decision.FailedOver);
    }

    [Fact]
    public void Failover_IsOneHopOnly_ASecondFailoverIsNeverFollowed()
    {
        // a -> failover b, and b's own route would fail over to c. For a client of a's route, c must never be used.
        ConfigParseResult result = ConfigParser.Parse("""
            {
              "cacheServers": [
                { "name": "a", "address": "10.0.0.1" },
                { "name": "b", "address": "10.0.0.2" },
                { "name": "c", "address": "10.0.0.3" }
              ],
              "routes": [
                { "priority": 1, "sourceCidr": "192.168.17.0/24", "server": "a", "failover": { "action": "failover", "server": "b" } },
                { "priority": 2, "sourceCidr": "192.168.18.0/24", "server": "b", "failover": { "action": "failover", "server": "c" } }
              ]
            }
            """);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        RoutingTable table = RoutingTable.Create(result.Config!);
        HealthSnapshot health = new(
        [
            new ServerHealth("a", HealthState.Unhealthy, IPAddress.Parse("10.0.0.1")),
            new ServerHealth("b", HealthState.Unhealthy, IPAddress.Parse("10.0.0.2")),
            new ServerHealth("c", HealthState.Healthy, IPAddress.Parse("10.0.0.3"))
        ]);

        Assert.Equal(RoutingOutcome.Passthrough, table.Resolve(Client17, health).Outcome);
        Assert.Equal("c", table.Resolve(Client18, health).Server!.Name); // c is only reachable through b's own route
    }

    [Fact]
    public void PassthroughRoute_PassesThrough_EvenWithHealthyServer()
    {
        RoutingDecision decision = Table("""{ "priority": 1, "sourceCidr": "192.168.17.0/24", "behavior": "passthrough" }""")
            .Resolve(Client17, Health(HealthState.Healthy));

        Assert.Equal(RoutingOutcome.Passthrough, decision.Outcome);
        Assert.Equal(1, decision.Route!.Priority);
    }

    [Fact]
    public void FirstMatchByPriorityWins_EvenIfALaterRowIsMoreSpecific()
    {
        RoutingTable table = Table("""
            { "priority": 20, "sourceCidr": "192.168.17.0/24", "server": "secondary" },
            { "priority": 10, "sourceCidr": "192.168.0.0/16",  "server": "primary" }
            """);

        RoutingDecision decision = table.Resolve(Client17, Health(HealthState.Healthy));

        Assert.Equal("primary", decision.Server!.Name);
    }

    [Fact]
    public void SpecificRowBeforeWideRow_RoutesEachClientToItsOwnServer()
    {
        RoutingTable table = Table("""
            { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary" },
            { "priority": 20, "sourceCidr": "192.168.0.0/16",  "server": "secondary" }
            """);
        HealthSnapshot health = Health(HealthState.Healthy);

        Assert.Equal("primary", table.Resolve(Client17, health).Server!.Name);
        Assert.Equal("secondary", table.Resolve(Client18, health).Server!.Name);
    }

    [Fact]
    public void DisabledRoute_IsSkipped()
    {
        RoutingTable table = Table("""
            { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary", "enabled": false },
            { "priority": 20, "sourceCidr": "192.168.0.0/16",  "server": "secondary" }
            """);

        Assert.Equal("secondary", table.Resolve(Client17, Health(HealthState.Healthy)).Server!.Name);
    }

    [Fact]
    public void ServerMissingFromSnapshot_IsNotUsable()
    {
        RoutingDecision decision = Table(Row17).Resolve(Client17, HealthSnapshot.Empty);

        Assert.Equal(RoutingOutcome.Passthrough, decision.Outcome);
    }

    [Fact]
    public void ServerNamesAreMatchedIgnoringCase()
    {
        HealthSnapshot health = new([new ServerHealth("PRIMARY", HealthState.Healthy, IPAddress.Parse("192.168.17.44"))]);

        Assert.Equal(RoutingOutcome.Cache, Table(Row17).Resolve(Client17, health).Outcome);
    }

    [Fact]
    public void SameTable_FollowsTheLatestHealthSnapshot()
    {
        RoutingTable table = Table(Row17WithFailover);

        Assert.Equal("primary", table.Resolve(Client17, Health(HealthState.Healthy)).Server!.Name);
        Assert.Equal("secondary", table.Resolve(Client17, Health(HealthState.Unhealthy)).Server!.Name);
        Assert.Equal("primary", table.Resolve(Client17, Health(HealthState.Healthy)).Server!.Name);
    }

    [Fact]
    public void HostRoute_MatchesOnlyThatAddress()
    {
        RoutingTable table = Table("""{ "priority": 1, "sourceCidr": "127.0.0.1/32", "server": "primary" }""");
        HealthSnapshot health = Health(HealthState.Healthy);

        Assert.Equal(RoutingOutcome.Cache, table.Resolve(IPAddress.Loopback, health).Outcome);
        Assert.Equal(RoutingOutcome.Passthrough, table.Resolve(IPAddress.Parse("127.0.0.2"), health).Outcome);
    }

    [Fact]
    public void Ipv4MappedClient_MatchesIpv4Route()
    {
        RoutingDecision decision = Table(Row17).Resolve(IPAddress.Parse("::ffff:192.168.17.50"), Health(HealthState.Healthy));

        Assert.Equal(RoutingOutcome.Cache, decision.Outcome);
    }

    [Fact]
    public void Ipv6Route_MatchesIpv6ClientOnly()
    {
        RoutingTable table = Table("""{ "priority": 1, "sourceCidr": "fd00:17::/64", "server": "primary" }""");
        HealthSnapshot health = Health(HealthState.Healthy);

        Assert.Equal(RoutingOutcome.Cache, table.Resolve(IPAddress.Parse("fd00:17::5"), health).Outcome);
        Assert.Equal(RoutingOutcome.Passthrough, table.Resolve(Client17, health).Outcome);
    }

    [Fact]
    public void Create_RejectsUnvalidatedConfigWithBadCidr()
    {
        RouterConfig config = new() { Routes = [new RouteConfig { Priority = 1, SourceCidr = "nonsense", Server = "primary" }] };

        Assert.Throws<ArgumentException>(() => RoutingTable.Create(config));
    }

    [Fact]
    public void CacheDecision_AlwaysCarriesAUsableServer()
    {
        RoutingTable table = Table(Row17WithFailover);

        foreach (HealthState state in Enum.GetValues<HealthState>())
        {
            RoutingDecision decision = table.Resolve(Client17, Health(state, state));

            if (decision.Outcome == RoutingOutcome.Cache)
                Assert.True(decision.Server!.IsUsable);
            else
                Assert.Null(decision.Server);
        }
    }

    [Fact]
    public void FailoverServer_IsIgnored_WhenTheActionIsPassthrough_EvenIfOneIsNamed()
    {
        // Accepted with a warning by the validator, so the table must not act on it.
        RoutingTable table = Table("""
            { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary", "failover": { "action": "passthrough", "server": "secondary" } }
            """);

        RoutingDecision decision = table.Resolve(Client17, Health(HealthState.Unhealthy, HealthState.Healthy));

        Assert.Equal(RoutingOutcome.Passthrough, decision.Outcome);
    }

    [Fact]
    public void Priorities_AreOrderedNumerically_IncludingNegativeAndExtremeValues()
    {
        RoutingTable table = Table("""
            { "priority": 2147483647, "sourceCidr": "0.0.0.0/0", "server": "secondary" },
            { "priority": -2147483648, "sourceCidr": "192.168.17.0/24", "server": "primary" },
            { "priority": 0, "sourceCidr": "192.168.0.0/16", "behavior": "passthrough" }
            """);
        HealthSnapshot health = Health(HealthState.Healthy);

        Assert.Equal("primary", table.Resolve(Client17, health).Server!.Name); // lowest number first, whatever the order in the file
        Assert.Equal(RoutingOutcome.Passthrough, table.Resolve(Client18, health).Outcome);
        Assert.Equal("secondary", table.Resolve(Other, health).Server!.Name);
    }

    [Fact]
    public void DecisionsAreIndependentOfTheOrderOfRowsInTheFile()
    {
        string[] rows =
        [
            """{ "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary" }""",
            """{ "priority": 20, "sourceCidr": "192.168.17.0/24", "server": "secondary" }""",
            """{ "priority": 30, "sourceCidr": "0.0.0.0/0", "behavior": "passthrough" }""",
        ];
        HealthSnapshot health = Health(HealthState.Healthy);

        foreach (string[] permutation in new[] { rows, [rows[2], rows[1], rows[0]], [rows[1], rows[2], rows[0]] })
            Assert.Equal("primary", Table(string.Join(",", permutation)).Resolve(Client17, health).Server!.Name);
    }

    [Fact]
    public void AServer_CanBeThePrimaryOfOneRoute_AndTheFailoverOfAnother()
    {
        RoutingTable table = Table("""
            { "priority": 1, "sourceCidr": "192.168.17.0/24", "server": "primary", "failover": { "action": "failover", "server": "secondary" } },
            { "priority": 2, "sourceCidr": "192.168.18.0/24", "server": "secondary", "failover": { "action": "failover", "server": "primary" } }
            """);

        Assert.Equal("secondary", table.Resolve(Client17, Health(HealthState.Unhealthy)).Server!.Name);
        Assert.Equal("primary", table.Resolve(Client18, Health(HealthState.Healthy, HealthState.Unhealthy)).Server!.Name);
        Assert.Equal("secondary", table.Resolve(Client18, Health(HealthState.Healthy)).Server!.Name);
    }

    [Fact]
    public void FailoverServerNames_AreMatchedIgnoringCase()
    {
        RoutingTable table = Table("""
            { "priority": 1, "sourceCidr": "192.168.17.0/24", "server": "PRIMARY", "failover": { "action": "failover", "server": "SeCoNdArY" } }
            """);

        Assert.Equal("secondary", table.Resolve(Client17, Health(HealthState.Unhealthy)).Server!.Name);
    }

    [Fact]
    public void Ipv6Loopback_And_UnspecifiedAddresses_AreHandledWithoutErrors()
    {
        RoutingTable table = Table("""
            { "priority": 1, "sourceCidr": "::1/128", "server": "primary" },
            { "priority": 2, "sourceCidr": "127.0.0.1/32", "server": "secondary" }
            """);
        HealthSnapshot health = Health(HealthState.Healthy);

        Assert.Equal("primary", table.Resolve(IPAddress.IPv6Loopback, health).Server!.Name);
        Assert.Equal("secondary", table.Resolve(IPAddress.Loopback, health).Server!.Name);
        Assert.Equal(RoutingOutcome.Passthrough, table.Resolve(IPAddress.Any, health).Outcome);
        Assert.Equal(RoutingOutcome.Passthrough, table.Resolve(IPAddress.IPv6Any, health).Outcome);
        Assert.Equal(RoutingOutcome.Passthrough, table.Resolve(IPAddress.Broadcast, health).Outcome);
    }

    [Fact]
    public void ALargeTable_StillPicksTheFirstMatch()
    {
        string rows = string.Join(",", Enumerable.Range(0, 3000).Select(i => $$"""{ "priority": {{i}}, "sourceCidr": "10.{{i / 250}}.{{i % 250}}.0/24", "server": "primary" }"""))
            + """, { "priority": 5000, "sourceCidr": "0.0.0.0/0", "server": "secondary" }""";
        RoutingTable table = Table(rows);
        HealthSnapshot health = Health(HealthState.Healthy);

        RoutingDecision inTable = table.Resolve(IPAddress.Parse("10.5.7.9"), health);
        RoutingDecision fallback = table.Resolve(IPAddress.Parse("172.16.0.1"), health);

        Assert.Equal(5 * 250 + 7, inTable.Route!.Priority);
        Assert.Equal("secondary", fallback.Server!.Name);
    }

    [Fact]
    public void Resolve_CanBeCalledFromManyThreadsWhileTheSnapshotChanges()
    {
        RoutingTable table = Table(Row17WithFailover);
        HealthSnapshot[] snapshots = [Health(HealthState.Healthy), Health(HealthState.Unhealthy), Health(HealthState.Unhealthy, HealthState.Unhealthy), Health(HealthState.Disabled)];
        int wrong = 0;

        Parallel.For(0, 20000, i =>
        {
            HealthSnapshot snapshot = snapshots[i % snapshots.Length];
            RoutingDecision decision = table.Resolve(Client17, snapshot);
            string? expected = (i % snapshots.Length) switch { 0 => "primary", 1 => "secondary", 2 => null, _ => "secondary" };

            if (decision.Server?.Name != expected)
                Interlocked.Increment(ref wrong);
        });

        Assert.Equal(0, wrong);
    }

    [Fact]
    public void CacheRouteWithoutAServer_IsPassedThrough_EvenWhenItWasNeverValidated()
    {
        RouterConfig config = new() { Routes = [new RouteConfig { Priority = 1, SourceCidr = "192.168.17.0/24", Behavior = RouteBehavior.CacheIfHealthy, Server = null }] };

        RoutingDecision decision = RoutingTable.Create(config).Resolve(Client17, Health(HealthState.Healthy));

        Assert.Equal(RoutingOutcome.Passthrough, decision.Outcome);
    }
}
