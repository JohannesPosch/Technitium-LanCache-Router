using System.Net;
using LanCacheRouter.Health;

namespace LanCacheRouter.Tests.Health;

public class HealthSnapshotTests
{
    private static ServerHealth Server(string name, HealthState state = HealthState.Healthy) => new(name, state, IPAddress.Parse("10.0.0.1"));

    [Fact]
    public void Empty_KnowsNoServer()
    {
        Assert.Empty(HealthSnapshot.Empty.Servers);
        Assert.Null(HealthSnapshot.Empty.Get("anything"));
    }

    [Fact]
    public void Get_IgnoresCase_AndListsEveryServer()
    {
        HealthSnapshot snapshot = new([Server("Cache-1"), Server("cache-2")]);

        Assert.Equal("Cache-1", snapshot.Get("CACHE-1")!.Name);
        Assert.Equal("cache-2", snapshot.Get("Cache-2")!.Name);
        Assert.Null(snapshot.Get("cache-3"));
        Assert.Equal(["Cache-1", "cache-2"], snapshot.Servers.Select(s => s.Name).Order());
    }

    [Fact]
    public void ServerNamesDifferingOnlyInCase_AreRefused_BecauseTheConfigurationValidatorForbidsThem()
    {
        Assert.Throws<ArgumentException>(() => new HealthSnapshot([Server("a"), Server("A")]));
    }

    [Theory]
    [InlineData(HealthState.Healthy, true)]
    [InlineData(HealthState.Unknown, false)]
    [InlineData(HealthState.Unhealthy, false)]
    [InlineData(HealthState.Disabled, false)]
    public void OnlyHealthyServers_AreUsable(HealthState state, bool usable)
    {
        Assert.Equal(usable, Server("s", state).IsUsable);
    }
}
