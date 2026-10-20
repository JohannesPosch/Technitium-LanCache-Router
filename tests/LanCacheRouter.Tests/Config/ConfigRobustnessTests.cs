using LanCacheRouter.Config;

namespace LanCacheRouter.Tests.Config;

/// <summary>Hand-edited JSON can contain nulls and odd values; the parser must report them as errors, never throw (ADR 0006).</summary>
public class ConfigRobustnessTests
{
    private const string Server = """{ "name": "a", "address": "192.168.1.1" }""";
    private const string Route = """{ "priority": 1, "sourceCidr": "192.168.1.0/24", "servers": ["a"] }""";

    public static TheoryData<string> HostileConfigs() => new()
    {
        """{ "cacheServers": [ null ] }""",
        """{ "cacheServers": null }""",
        """{ "routes": [ null ] }""",
        """{ "routes": null }""",
        """{ "cacheServers": [ { "name": null, "address": "192.168.1.1" } ] }""",
        """{ "cacheServers": [ { "name": "a", "address": null } ] }""",
        """{ "cacheServers": [ { "name": "a", "address": "192.168.1.1", "healthCheck": { "expectedStatus": null, "type": null } } ] }""",
        """{ "cacheServers": [ { "name": "a", "address": "192.168.1.1", "healthCheck": { "expectedStatus": [ null ] } } ] }""",
        """{ "cacheServers": [ { "name": "a", "address": "192.168.1.1", "healthCheck": { "startupProbe": null } } ] }""",
        """{ "cacheServers": [ { "name": "a", "address": "192.168.1.1" } ], "routes": [ { "priority": 1, "sourceCidr": null, "servers": ["a"] } ] }""",
        """{ "cacheServers": [ { "name": "a", "address": "192.168.1.1" } ], "routes": [ { "priority": 1, "sourceCidr": "10.0.0.0/8", "servers": null } ] }""",
        """{ "cacheServers": [ { "name": "a", "address": "192.168.1.1" } ], "routes": [ { "priority": 1, "sourceCidr": "10.0.0.0/8", "servers": ["a", null] } ] }""",
        """{ "domainList": null }""",
        """{ "domainList": { "baseUrl": null } }""",
        """{ "domainList": { "refreshCron": null } }""",
        """{ "domainList": { "services": null } }""",
        """{ "domainList": { "services": { "include": null, "exclude": null } } }""",
        """{ "domainList": { "services": { "include": [ null ] } } }""",
        """{ "domainList": { "extraDomains": null } }""",
        """{ "domainList": { "extraDomains": [ null ] } }""",
        """{ "domainList": { "extraDomains": [ { "domain": null, "service": null } ] } }""",
        """{ "domainList": { "excludeDomains": null } }""",
        """{ "domainList": { "excludeDomains": [ null ] } }""",
        """{ "dns": null }""",
        """{ "logging": null }""",
        """{ "security": null }""",
        """{ "healthCheckDefaults": { "expectedStatus": [ null ] } }""",
        """{ "enabled": "yes" }""",
        """{ "routes": [ { "priority": 99999999999 } ] }""",
        """[]""",
        "\"text\"",
        """42""",
    };

    [Theory]
    [MemberData(nameof(HostileConfigs))]
    public void HostileInput_NeverThrows_AndNeverYieldsAnUnvalidatedConfig(string json)
    {
        ConfigParseResult result = ConfigParser.Parse(json);

        Assert.Equal(result.Config is not null, result.IsValid);
        Assert.Equal(result.IsValid, result.Errors.Count == 0);
    }
}
