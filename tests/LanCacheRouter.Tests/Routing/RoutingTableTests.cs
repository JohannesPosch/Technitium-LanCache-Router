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
                { "name": "secondary", "address": "192.168.18.44" },
                { "name": "a", "address": "10.0.0.1" },
                { "name": "b", "address": "10.0.0.2" },
                { "name": "c", "address": "10.0.0.3" }
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

    private const string Row17 = """{ "priority": 10, "sourceCidr": "192.168.17.0/24", "servers": ["primary"] }""";

    private const string Row17WithFailover = """
        { "priority": 10, "sourceCidr": "192.168.17.0/24", "servers": ["primary", "secondary"] }
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
    public void UnusablePrimary_WithoutFurtherServers_IsPassedThrough(HealthState state)
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
    public void OtherRoutes_AreNeverConsulted_WhenTheListIsExhausted()
    {
        // The list of a's route is [a, b]; b's own route lists c. For a client of a's route, c must never be used.
        ConfigParseResult result = ConfigParser.Parse("""
            {
              "cacheServers": [
                { "name": "a", "address": "10.0.0.1" },
                { "name": "b", "address": "10.0.0.2" },
                { "name": "c", "address": "10.0.0.3" }
              ],
              "routes": [
                { "priority": 1, "sourceCidr": "192.168.17.0/24", "servers": ["a", "b"] },
                { "priority": 2, "sourceCidr": "192.168.18.0/24", "servers": ["b", "c"] }
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
        Assert.Equal("c", table.Resolve(Client18, health).Server!.Name); // c is only listed on b's own route
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
            { "priority": 20, "sourceCidr": "192.168.17.0/24", "servers": ["secondary"] },
            { "priority": 10, "sourceCidr": "192.168.0.0/16",  "servers": ["primary"] }
            """);

        RoutingDecision decision = table.Resolve(Client17, Health(HealthState.Healthy));

        Assert.Equal("primary", decision.Server!.Name);
    }

    [Fact]
    public void SpecificRowBeforeWideRow_RoutesEachClientToItsOwnServer()
    {
        RoutingTable table = Table("""
            { "priority": 10, "sourceCidr": "192.168.17.0/24", "servers": ["primary"] },
            { "priority": 20, "sourceCidr": "192.168.0.0/16",  "servers": ["secondary"] }
            """);
        HealthSnapshot health = Health(HealthState.Healthy);

        Assert.Equal("primary", table.Resolve(Client17, health).Server!.Name);
        Assert.Equal("secondary", table.Resolve(Client18, health).Server!.Name);
    }

    [Fact]
    public void DisabledRoute_IsSkipped()
    {
        RoutingTable table = Table("""
            { "priority": 10, "sourceCidr": "192.168.17.0/24", "servers": ["primary"], "enabled": false },
            { "priority": 20, "sourceCidr": "192.168.0.0/16",  "servers": ["secondary"] }
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
        RoutingTable table = Table("""{ "priority": 1, "sourceCidr": "127.0.0.1/32", "servers": ["primary"] }""");
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
        RoutingTable table = Table("""{ "priority": 1, "sourceCidr": "fd00:17::/64", "servers": ["primary"] }""");
        HealthSnapshot health = Health(HealthState.Healthy);

        Assert.Equal(RoutingOutcome.Cache, table.Resolve(IPAddress.Parse("fd00:17::5"), health).Outcome);
        Assert.Equal(RoutingOutcome.Passthrough, table.Resolve(Client17, health).Outcome);
    }

    [Fact]
    public void Create_RejectsUnvalidatedConfigWithBadCidr()
    {
        RouterConfig config = new() { Routes = [new RouteConfig { Priority = 1, SourceCidr = "nonsense", Servers = ["primary"] }] };

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

    private const string Row17WithThreeServers = """
        { "priority": 10, "sourceCidr": "192.168.17.0/24", "servers": ["a", "b", "c"] }
        """;

    private static HealthSnapshot Health3(HealthState a, HealthState b, HealthState c) => new(
    [
        new ServerHealth("a", a, IPAddress.Parse("10.0.0.1")),
        new ServerHealth("b", b, IPAddress.Parse("10.0.0.2")),
        new ServerHealth("c", c, IPAddress.Parse("10.0.0.3"))
    ]);

    [Theory]
    [InlineData(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, "a", 1)]
    [InlineData(HealthState.Healthy, HealthState.Unhealthy, HealthState.Unhealthy, "a", 1)]
    [InlineData(HealthState.Unhealthy, HealthState.Healthy, HealthState.Healthy, "b", 2)]
    [InlineData(HealthState.Unhealthy, HealthState.Unknown, HealthState.Healthy, "c", 3)]
    [InlineData(HealthState.Disabled, HealthState.Unhealthy, HealthState.Healthy, "c", 3)]
    public void ThreeServers_FirstHealthyInListOrderWins(HealthState a, HealthState b, HealthState c, string expected, int position)
    {
        RoutingDecision decision = Table(Row17WithThreeServers).Resolve(Client17, Health3(a, b, c));

        Assert.Equal(RoutingOutcome.Cache, decision.Outcome);
        Assert.Equal(expected, decision.Server!.Name);
        Assert.Equal(position, decision.Position);
        Assert.Equal(position > 1, decision.FailedOver);
    }

    [Fact]
    public void ThreeServers_AllUnusable_IsPassedThrough()
    {
        RoutingDecision decision = Table(Row17WithThreeServers).Resolve(Client17, Health3(HealthState.Unhealthy, HealthState.Unknown, HealthState.Disabled));

        Assert.Equal(RoutingOutcome.Passthrough, decision.Outcome);
        Assert.Equal(0, decision.Position);
    }

    [Fact]
    public void RecoveredPreferredServer_TakesTrafficBackAtOnce()
    {
        RoutingTable table = Table(Row17WithThreeServers);

        Assert.Equal("c", table.Resolve(Client17, Health3(HealthState.Unhealthy, HealthState.Unhealthy, HealthState.Healthy)).Server!.Name);
        Assert.Equal("a", table.Resolve(Client17, Health3(HealthState.Healthy, HealthState.Unhealthy, HealthState.Healthy)).Server!.Name);
    }

    [Fact]
    public void UnknownServerNames_AreSkipped()
    {
        // Rejected by the validator, so the table is built directly; it must still not choose a server it cannot find.
        RouterConfig config = new() { Routes = [new RouteConfig { Priority = 10, SourceCidr = "192.168.17.0/24", Servers = ["ghost", "secondary"] }] };
        RoutingTable table = RoutingTable.Create(config);

        Assert.Equal("secondary", table.Resolve(Client17, Health(HealthState.Healthy)).Server!.Name);
    }

    [Fact]
    public void Priorities_AreOrderedNumerically_IncludingNegativeAndExtremeValues()
    {
        RoutingTable table = Table("""
            { "priority": 2147483647, "sourceCidr": "0.0.0.0/0", "servers": ["secondary"] },
            { "priority": -2147483648, "sourceCidr": "192.168.17.0/24", "servers": ["primary"] },
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
            """{ "priority": 10, "sourceCidr": "192.168.17.0/24", "servers": ["primary"] }""",
            """{ "priority": 20, "sourceCidr": "192.168.17.0/24", "servers": ["secondary"] }""",
            """{ "priority": 30, "sourceCidr": "0.0.0.0/0", "behavior": "passthrough" }""",
        ];
        HealthSnapshot health = Health(HealthState.Healthy);

        foreach (string[] permutation in new[] { rows, [rows[2], rows[1], rows[0]], [rows[1], rows[2], rows[0]] })
            Assert.Equal("primary", Table(string.Join(",", permutation)).Resolve(Client17, health).Server!.Name);
    }

    [Fact]
    public void AServer_CanBeFirstOnOneRoute_AndLaterOnAnother()
    {
        RoutingTable table = Table("""
            { "priority": 1, "sourceCidr": "192.168.17.0/24", "servers": ["primary", "secondary"] },
            { "priority": 2, "sourceCidr": "192.168.18.0/24", "servers": ["secondary", "primary"] }
            """);

        Assert.Equal("secondary", table.Resolve(Client17, Health(HealthState.Unhealthy)).Server!.Name);
        Assert.Equal("primary", table.Resolve(Client18, Health(HealthState.Healthy, HealthState.Unhealthy)).Server!.Name);
        Assert.Equal("secondary", table.Resolve(Client18, Health(HealthState.Healthy)).Server!.Name);
    }

    [Fact]
    public void ServerNamesInTheList_AreMatchedIgnoringCase()
    {
        RoutingTable table = Table("""
            { "priority": 1, "sourceCidr": "192.168.17.0/24", "servers": ["PRIMARY", "SeCoNdArY"] }
            """);

        Assert.Equal("secondary", table.Resolve(Client17, Health(HealthState.Unhealthy)).Server!.Name);
    }

    [Fact]
    public void Ipv6Loopback_And_UnspecifiedAddresses_AreHandledWithoutErrors()
    {
        RoutingTable table = Table("""
            { "priority": 1, "sourceCidr": "::1/128", "servers": ["primary"] },
            { "priority": 2, "sourceCidr": "127.0.0.1/32", "servers": ["secondary"] }
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
        string rows = string.Join(",", Enumerable.Range(0, 3000).Select(i => $$"""{ "priority": {{i}}, "sourceCidr": "10.{{i / 250}}.{{i % 250}}.0/24", "servers": ["primary"] }"""))
            + """, { "priority": 5000, "sourceCidr": "0.0.0.0/0", "servers": ["secondary"] }""";
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
    public void CacheRouteWithoutServers_IsPassedThrough_EvenWhenItWasNeverValidated()
    {
        RouterConfig config = new() { Routes = [new RouteConfig { Priority = 1, SourceCidr = "192.168.17.0/24", Behavior = RouteBehavior.CacheIfHealthy, Servers = [] }] };

        RoutingDecision decision = RoutingTable.Create(config).Resolve(Client17, Health(HealthState.Healthy));

        Assert.Equal(RoutingOutcome.Passthrough, decision.Outcome);
    }
}
