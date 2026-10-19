using LanCacheRouter.Config;

namespace LanCacheRouter.Tests.Config;

public class ConfigParserTests
{
    // Mirrors the reference configuration in ADR 0006 (with the comments kept on purpose).
    private const string ReferenceConfig = """
        {
          "enabled": true, // master switch
          "domainList": {
            "baseUrl": "https://raw.githubusercontent.com/uklans/cache-domains/master/",
            "refreshCron": "0 4 * * *",
            "refreshOnStartup": true,
            "httpTimeoutSeconds": 30,
            "services": { "include": [], "exclude": [] },
            "extraDomains": [ { "domain": "*.example-cdn.com", "service": "custom" } ],
            "excludeDomains": []
          },
          "healthCheckDefaults": {
            "type": "http",
            "port": 80,
            "path": "/lancache-heartbeat",
            "expectedStatus": [200, 204],
            "intervalSeconds": 10,
            "timeoutSeconds": 2,
            "failThreshold": 2,
            "successThreshold": 3,
            "verbose": false,
            "startupProbe": { "enabled": true, "fastIntervalSeconds": 1 }
          },
          "cacheServers": [
            { "name": "monolith-1", "enabled": true, "address": "192.168.17.44",
              "healthCheck": { "type": "tcp" } },
            { "name": "monolith-2", "enabled": false, "address": "192.168.18.44" }
          ],
          "routes": [
            { "priority": 10, "sourceCidr": "127.0.0.1/32", "server": "monolith-1", "behavior": "cacheIfHealthy",
              "failover": { "action": "passthrough" }, "comment": "local tests" },
            { "priority": 20, "sourceCidr": "192.168.17.0/24", "server": "monolith-1", "behavior": "cacheIfHealthy",
              "failover": { "action": "failover", "server": "monolith-2" } },
          ],
          "dns": { "ttl": 60, "aaaaMode": "nodata", "httpsMode": "nodata" },
          "logging": { "queries": false }
        }
        """;

    private const string Minimal = """
        {
          "cacheServers": [ { "name": "a", "address": "10.0.0.1" }, { "name": "b", "address": "10.0.0.2" } ],
          "routes": [ { "priority": 1, "sourceCidr": "10.1.0.0/16", "server": "a" } ]
        }
        """;

    private static ConfigParseResult Parse(string json) => ConfigParser.Parse(json);

    private static string Replace(string json, string from, string to)
    {
        Assert.Contains(from, json);
        return json.Replace(from, to);
    }

    [Fact]
    public void ReferenceConfig_IsValid()
    {
        ConfigParseResult result = Parse(ReferenceConfig);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Config!.CacheServers.Count);
        Assert.Equal(FailoverAction.Failover, result.Config.Routes[1].Failover.Action);
    }

    [Fact]
    public void MinimalConfig_UsesDefaults()
    {
        ConfigParseResult result = Parse(Minimal);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        RouterConfig config = result.Config!;
        Assert.True(config.Enabled);
        Assert.Equal("0 4 * * *", config.DomainList.RefreshCron);
        Assert.Equal(60, config.Dns.Ttl);
        Assert.Equal(RouteBehavior.CacheIfHealthy, config.Routes[0].Behavior);
        Assert.Equal(ResolvedHealthCheck.BuiltIn, config.ResolveHealthCheck(config.CacheServers[0]));
    }

    [Fact]
    public void HealthCheck_ServerOverridesDefaultsOverBuiltIn()
    {
        ConfigParseResult result = Parse(ReferenceConfig);
        RouterConfig config = result.Config!;

        ResolvedHealthCheck monolith1 = config.ResolveHealthCheck(config.CacheServers[0]);
        Assert.Equal(ProbeType.Tcp, monolith1.Type); // server override
        Assert.Equal(80, monolith1.Port);            // from defaults
        Assert.Equal(3, monolith1.SuccessThreshold);

        ResolvedHealthCheck monolith2 = config.ResolveHealthCheck(config.CacheServers[1]);
        Assert.Equal(ProbeType.Http, monolith2.Type);
        Assert.Equal("/lancache-heartbeat", monolith2.Path);
    }

    [Fact]
    public void PartialDefaults_KeepBuiltInForOmittedValues()
    {
        string json = Replace(Minimal, "\"routes\"", "\"healthCheckDefaults\": { \"intervalSeconds\": 5, \"startupProbe\": { \"fastIntervalSeconds\": 2 } }, \"routes\"");
        RouterConfig config = Parse(json).Config!;

        ResolvedHealthCheck check = config.ResolveHealthCheck(config.CacheServers[0]);
        Assert.Equal(5, check.IntervalSeconds);
        Assert.Equal(2, check.StartupFastIntervalSeconds);
        Assert.True(check.StartupProbeEnabled);
        Assert.Equal(2, check.FailThreshold);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ not json")]
    public void Garbage_IsRejectedWithoutThrowing(string? json)
    {
        ConfigParseResult result = Parse(json!);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void UnknownProperty_IsRejected()
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"routes\"", "\"rotes\": [], \"routes\""));

        Assert.False(result.IsValid);
        Assert.Contains("rotes", result.Errors[0]);
    }

    [Theory]
    [InlineData("\"domainList\": { \"timeZone\": \"UTC\" }", "timeZone")]
    [InlineData("\"domainList\": { \"minDomainRatio\": 0.5 }", "minDomainRatio")]
    [InlineData("\"dns\": { \"rotate\": true }", "rotate")]
    public void RemovedSettings_AreRejectedAsUnknown(string property, string name)
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"routes\"", property + ", \"routes\""));

        Assert.False(result.IsValid);
        Assert.Contains(name, result.Errors[0]);
    }

    [Fact]
    public void UnknownEnumValue_IsRejected()
    {
        Assert.False(Parse(Replace(Minimal, "\"sourceCidr\"", "\"behavior\": \"always\", \"sourceCidr\"")).IsValid);
    }

    [Fact]
    public void Disabled_ConfigWithoutServersIsValid()
    {
        ConfigParseResult result = Parse("""{ "enabled": false }""");

        Assert.True(result.IsValid);
        Assert.False(result.Config!.Enabled);
    }

    [Theory]
    [InlineData("\"server\": \"a\" }", "\"server\": \"nope\" }", "does not exist")]
    [InlineData("\"sourceCidr\": \"10.1.0.0/16\"", "\"sourceCidr\": \"10.1.0.0/40\"", "not a valid CIDR")]
    [InlineData("\"server\": \"a\" }", "\"server\": \"a\", \"failover\": { \"action\": \"failover\" } }", "requires a server")]
    [InlineData("\"server\": \"a\" }", "\"server\": \"a\", \"failover\": { \"action\": \"failover\", \"server\": \"a\" } }", "must differ")]
    [InlineData("\"server\": \"a\" }", "\"server\": \"a\", \"failover\": { \"action\": \"failover\", \"server\": \"zzz\" } }", "does not exist")]
    [InlineData("\"server\": \"a\" }", "\"behavior\": \"cacheIfHealthy\" }", "requires a server")]
    public void Route_Errors(string from, string to, string expectedError)
    {
        ConfigParseResult result = Parse(Replace(Minimal, from, to));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expectedError));
    }

    [Fact]
    public void DuplicatePriority_IsRejected()
    {
        string json = Replace(Minimal, "\"routes\": [", "\"routes\": [ { \"priority\": 1, \"sourceCidr\": \"10.2.0.0/16\", \"server\": \"b\" },");
        ConfigParseResult result = Parse(json);

        Assert.Contains(result.Errors, e => e.Contains("used more than once"));
    }

    [Fact]
    public void ShadowedRoute_ProducesWarningNotError()
    {
        string json = Replace(Minimal, "\"routes\": [", "\"routes\": [ { \"priority\": 2, \"sourceCidr\": \"10.1.5.0/24\", \"server\": \"b\" },");
        ConfigParseResult result = Parse(json);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("shadowed by priority 1"));
    }

    [Fact]
    public void ShadowedByDisabledRoute_IsNotWarned()
    {
        string json = Replace(Minimal, "\"priority\": 1,", "\"priority\": 1, \"enabled\": false,")
            .Replace("\"routes\": [", "\"routes\": [ { \"priority\": 2, \"sourceCidr\": \"10.1.5.0/24\", \"server\": \"b\" },");
        ConfigParseResult result = Parse(json);

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void PassthroughRoute_NeedsNoServer()
    {
        string json = Replace(Minimal, "\"server\": \"a\"", "\"behavior\": \"passthrough\"");

        Assert.True(Parse(json).IsValid);
    }

    [Theory]
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"fd00::1\"", "not a valid IPv4")]
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"10.0.0\"", "not a valid IPv4")]
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"\"", "not a valid IPv4")]
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"0.0.0.0\"", "not a usable")]
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"224.0.0.1\"", "not a usable")] // multicast
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"255.255.255.255\"", "not a usable")]
    [InlineData("\"address\": \"10.0.0.1\"", "\"addresses\": [\"10.0.0.1\"]", "addresses")] // the old multi-address property is gone
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"10.0.0.1\", \"healthCheck\": { \"port\": 0 }", "port")]
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"10.0.0.1\", \"healthCheck\": { \"timeoutSeconds\": 60 }", "must not exceed")]
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"10.0.0.1\", \"healthCheck\": { \"path\": \"x\" }", "start with")]
    [InlineData("\"address\": \"10.0.0.1\"", "\"address\": \"10.0.0.1\", \"healthCheck\": { \"type\": \"icmp\" }", "healthCheck.type")] // icmp dropped from v1
    public void CacheServer_Errors(string from, string to, string expectedError)
    {
        ConfigParseResult result = Parse(Replace(Minimal, from, to));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expectedError));
    }

    [Fact]
    public void DuplicateServerNames_AreRejectedCaseInsensitively()
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"name\": \"b\"", "\"name\": \"A\""));

        Assert.Contains(result.Errors, e => e.Contains("duplicate server name"));
    }

    [Theory]
    [InlineData("\"refreshCron\": \"every day\"", "refreshCron")]
    [InlineData("\"refreshCron\": \"61 4 * * *\"", "refreshCron is invalid")]
    [InlineData("\"baseUrl\": \"ftp://x/\"", "baseUrl")]
    [InlineData("\"httpTimeoutSeconds\": 0", "httpTimeoutSeconds")]
    [InlineData("\"extraDomains\": [ { \"domain\": \"*.com\" } ]", "extraDomains")]
    [InlineData("\"excludeDomains\": [ \"bad domain\" ]", "excludeDomains")]
    [InlineData("\"services\": { \"include\": [\"steam\"], \"exclude\": [\"Steam\"] }", "both included and excluded")]
    public void DomainList_Errors(string property, string expectedError)
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"routes\"", $"\"domainList\": {{ {property} }}, \"routes\""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expectedError));
    }

    [Theory]
    [InlineData("0 4 * * *")]
    [InlineData("*/5 * * * *")]
    [InlineData("30 0 4 * * *")] // 6 fields = with seconds
    public void Cron_AcceptsFiveAndSixFields(string cron)
    {
        Assert.True(Parse(Replace(Minimal, "\"routes\"", $"\"domainList\": {{ \"refreshCron\": \"{cron}\" }}, \"routes\"")).IsValid);
    }

    [Fact]
    public void DnsTtl_IsRangeChecked()
    {
        Assert.False(Parse(Replace(Minimal, "\"routes\"", "\"dns\": { \"ttl\": 0 }, \"routes\"")).IsValid);
    }

    [Fact]
    public void AllErrorsAreCollected()
    {
        string json = Replace(Minimal, "\"routes\"", "\"dns\": { \"ttl\": 0 }, \"domainList\": { \"httpTimeoutSeconds\": 0 }, \"routes\"");

        Assert.True(Parse(json).Errors.Count >= 2);
    }

    private static string WithDefaults(string healthCheck) => Replace(Minimal, "\"routes\"", $"\"healthCheckDefaults\": {{ {healthCheck} }}, \"routes\"");

    private static string WithServerCheck(string healthCheck) => Replace(Minimal, "\"name\": \"a\", \"address\": \"10.0.0.1\"", $"\"name\": \"a\", \"address\": \"10.0.0.1\", \"healthCheck\": {{ {healthCheck} }}");

    [Theory]
    [InlineData("\"port\": 0", "port")]
    [InlineData("\"port\": 65536", "port")]
    [InlineData("\"port\": -1", "port")]
    [InlineData("\"path\": \"\"", "path")]
    [InlineData("\"path\": \"heartbeat\"", "path")]
    [InlineData("\"expectedStatus\": []", "expectedStatus")]
    [InlineData("\"expectedStatus\": [99]", "expectedStatus")]
    [InlineData("\"expectedStatus\": [200, 600]", "expectedStatus")]
    [InlineData("\"intervalSeconds\": 0", "intervalSeconds")]
    [InlineData("\"intervalSeconds\": -5", "intervalSeconds")]
    [InlineData("\"intervalSeconds\": 86401", "intervalSeconds")] // ~50 days and more cannot be waited for in one go
    [InlineData("\"timeoutSeconds\": 0", "timeoutSeconds")]
    [InlineData("\"timeoutSeconds\": 86401", "timeoutSeconds")]
    [InlineData("\"failThreshold\": 0", "failThreshold")]
    [InlineData("\"successThreshold\": 0", "successThreshold")]
    [InlineData("\"startupProbe\": { \"fastIntervalSeconds\": 0 }", "fastIntervalSeconds")]
    [InlineData("\"startupProbe\": { \"fastIntervalSeconds\": 86401 }", "fastIntervalSeconds")]
    public void HealthCheckValues_AreRangeChecked_InTheDefaultsAndPerServer(string setting, string name)
    {
        ConfigParseResult inDefaults = Parse(WithDefaults(setting));
        ConfigParseResult onServer = Parse(WithServerCheck(setting));

        Assert.False(inDefaults.IsValid);
        Assert.Contains(inDefaults.Errors, e => e.StartsWith("healthCheckDefaults.") && e.Contains(name));
        Assert.False(onServer.IsValid);
        Assert.Contains(onServer.Errors, e => e.Contains("cacheServers['a'].healthCheck.") && e.Contains(name));
    }

    [Theory]
    [InlineData("\"port\": 1")]
    [InlineData("\"port\": 65535")]
    [InlineData("\"path\": \"/\"")]
    [InlineData("\"expectedStatus\": [100, 599]")]
    [InlineData("\"intervalSeconds\": 86400, \"timeoutSeconds\": 86400")]
    [InlineData("\"intervalSeconds\": 1, \"timeoutSeconds\": 1, \"startupProbe\": { \"fastIntervalSeconds\": 86400 }")]
    [InlineData("\"failThreshold\": 1000000, \"successThreshold\": 1000000")]
    public void HealthCheckValues_AtTheEdgesOfTheirRange_AreAccepted(string setting)
    {
        Assert.True(Parse(WithDefaults(setting)).IsValid, string.Join("; ", Parse(WithDefaults(setting)).Errors));
    }

    [Fact]
    public void TimeoutAboveInterval_IsDetectedOnTheEffectiveValues_ThatCombineDefaultsAndServerSettings()
    {
        // Neither block is wrong alone: the default timeout is 2 s, the default interval is lowered to 1 s.
        ConfigParseResult viaInterval = Parse(WithDefaults("\"intervalSeconds\": 1"));
        Assert.Contains(viaInterval.Errors, e => e.Contains("cacheServers['a']") && e.Contains("timeoutSeconds (2) must not exceed intervalSeconds (1)"));

        // The defaults allow a 20 s timeout; one server shortens its interval below that.
        string serverInterval = Replace(WithDefaults("\"intervalSeconds\": 30, \"timeoutSeconds\": 20"), "\"name\": \"b\", \"address\": \"10.0.0.2\"",
            "\"name\": \"b\", \"address\": \"10.0.0.2\", \"healthCheck\": { \"intervalSeconds\": 10 }");
        ConfigParseResult viaServer = Parse(serverInterval);
        Assert.Single(viaServer.Errors);
        Assert.Contains("cacheServers['b']", viaServer.Errors[0]);

        Assert.True(Parse(WithDefaults("\"intervalSeconds\": 1, \"timeoutSeconds\": 1")).IsValid);
    }

    [Fact]
    public void RouteWithPassthroughBehavior_AndAServer_OnlyWarns()
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"server\": \"a\"", "\"behavior\": \"passthrough\", \"server\": \"a\""));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w == "routes[priority=1]: server is ignored because behavior is passthrough");
    }

    [Fact]
    public void FailoverServer_WithPassthroughAction_OnlyWarns()
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"server\": \"a\"", "\"server\": \"a\", \"failover\": { \"action\": \"passthrough\", \"server\": \"b\" }"));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w == "routes[priority=1].failover: server is ignored because action is passthrough");
    }

    [Fact]
    public void PropertyNames_AreCaseSensitive()
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"routes\"", "\"Enabled\": false, \"routes\""));

        Assert.False(result.IsValid);
        Assert.Contains("Enabled", result.Errors[0]);
    }

    [Theory]
    [InlineData("\"behavior\": 1")]
    [InlineData("\"behavior\": \"\"")]
    [InlineData("\"behavior\": null")]
    [InlineData("\"failover\": { \"action\": 0 }")]
    public void EnumValues_MustBeKnownStrings(string setting)
    {
        Assert.False(Parse(Replace(Minimal, "\"sourceCidr\"", setting + ", \"sourceCidr\"")).IsValid);
    }

    [Theory]
    [InlineData("0  4 * * *")] // repeated blanks
    [InlineData(" 0 4 * * * ")]
    [InlineData("0 4 * * MON")]
    [InlineData("0 4 * JAN-MAR *")]
    [InlineData("0 4 1 */3 *")] // quarterly
    [InlineData("0 0 1 1 *")] // yearly
    [InlineData("*/10 * * * * *")] // every ten seconds
    [InlineData("0 0 31 2 *")] // syntactically fine, although it never occurs
    public void RefreshCron_AcceptedForms(string cron)
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"routes\"", $"\"domainList\": {{ \"refreshCron\": \"{cron}\" }}, \"routes\""));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0 4 * *")] // four fields
    [InlineData("0 0 0 4 * * *")] // seven fields
    [InlineData("@daily")]
    [InlineData("60 * * * *")]
    [InlineData("* * * * 8")]
    [InlineData("0 4 * * * ; rm -rf /")]
    public void RefreshCron_RejectedForms(string cron)
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"routes\"", $"\"domainList\": {{ \"refreshCron\": \"{cron}\" }}, \"routes\""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("refreshCron"));
    }

    [Theory]
    [InlineData("https://example.test/cache-domains/master")] // the missing slash is added later
    [InlineData("http://10.0.0.5:8080/lists/")]
    [InlineData("HTTPS://EXAMPLE.TEST/x/")]
    public void BaseUrl_AcceptedForms(string url)
    {
        Assert.True(Parse(Replace(Minimal, "\"routes\"", $"\"domainList\": {{ \"baseUrl\": \"{url}\" }}, \"routes\"")).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.test/x/")]
    [InlineData("/x/")]
    [InlineData("file:///etc/lists/")]
    [InlineData("ftp://example.test/")]
    [InlineData("javascript:alert(1)")]
    public void BaseUrl_RejectedForms(string url)
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"routes\"", $"\"domainList\": {{ \"baseUrl\": \"{url}\" }}, \"routes\""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("baseUrl"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(86400, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(86401, false)]
    public void DnsTtl_Boundaries(int ttl, bool valid)
    {
        Assert.Equal(valid, Parse(Replace(Minimal, "\"routes\"", $"\"dns\": {{ \"ttl\": {ttl} }}, \"routes\"")).IsValid);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(86400, true)]
    [InlineData(86401, false)]
    public void HttpTimeout_UpperBound(int seconds, bool valid)
    {
        Assert.Equal(valid, Parse(Replace(Minimal, "\"routes\"", $"\"domainList\": {{ \"httpTimeoutSeconds\": {seconds} }}, \"routes\"")).IsValid);
    }

    [Theory]
    [InlineData("\"name\": \"\"")]
    [InlineData("\"name\": \"   \"")]
    public void ServerWithoutAName_IsRejected(string name)
    {
        ConfigParseResult result = Parse(Replace(Minimal, "\"name\": \"b\"", name));

        Assert.Contains(result.Errors, e => e.Contains("needs a name"));
    }

    [Fact]
    public void RoutesMayPointAtDisabledServers_TheyJustNeverServe()
    {
        string json = Replace(Replace(Minimal, "\"name\": \"a\"", "\"name\": \"a\", \"enabled\": false"),
            "\"server\": \"a\" }", "\"server\": \"a\", \"failover\": { \"action\": \"failover\", \"server\": \"b\" } }");

        Assert.True(Parse(json).IsValid);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void AnyIntegerIsAValidPriority(int priority)
    {
        Assert.True(Parse(Replace(Minimal, "\"priority\": 1", $"\"priority\": {priority}")).IsValid);
    }

    [Fact]
    public void EmptyConfigurationObject_IsValid_AndHasNothingToDo()
    {
        ConfigParseResult result = Parse("{}");

        Assert.True(result.IsValid);
        Assert.Empty(result.Config!.CacheServers);
        Assert.Empty(result.Config.Routes);
    }

    [Fact]
    public void BigConfiguration_IsValidatedQuickly()
    {
        string servers = string.Join(",", Enumerable.Range(0, 500).Select(i => $$"""{ "name": "s{{i}}", "address": "10.{{i / 250}}.{{i % 250}}.1" }"""));
        string routes = string.Join(",", Enumerable.Range(0, 2000).Select(i => $$"""{ "priority": {{i}}, "sourceCidr": "10.{{i / 250}}.{{i % 250}}.0/24", "server": "s{{i % 500}}" }"""));

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        ConfigParseResult result = Parse($$"""{ "cacheServers": [ {{servers}} ], "routes": [ {{routes}} ] }""");
        watch.Stop();

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Take(3)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void DeeplyNestedJson_IsReportedNotThrown()
    {
        string json = string.Concat(Enumerable.Repeat("[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000));

        ConfigParseResult result = Parse(json);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ResolvedHealthCheck_EqualsAndHashCode_FollowTheValues()
    {
        ResolvedHealthCheck a = ResolvedHealthCheck.BuiltIn with { ExpectedStatus = [200, 204] };
        ResolvedHealthCheck sameValues = ResolvedHealthCheck.BuiltIn with { ExpectedStatus = new List<int> { 200, 204 } };
        ResolvedHealthCheck otherStatus = a with { ExpectedStatus = [200] };
        ResolvedHealthCheck otherPort = a with { Port = 8080 };
        ResolvedHealthCheck otherVerbose = a with { Verbose = true };

        Assert.True(a.Equals(sameValues));
        Assert.Equal(a.GetHashCode(), sameValues.GetHashCode());
        Assert.False(a.Equals(otherStatus));
        Assert.False(a.Equals(otherPort));
        Assert.False(a.Equals(otherVerbose));
        Assert.False(a.Equals(null));
        Assert.Equal(a, a.Apply(null));
        Assert.Same(a, a.Apply(null));
    }

    [Fact]
    public void ResolvedHealthCheck_Apply_ReplacesOnlyTheValuesThatAreSet()
    {
        HealthCheckSettings overrides = new() { Port = 8080, StartupProbe = new StartupProbeSettings { Enabled = false } };

        ResolvedHealthCheck merged = ResolvedHealthCheck.BuiltIn.Apply(overrides);

        Assert.Equal(8080, merged.Port);
        Assert.False(merged.StartupProbeEnabled);
        Assert.Equal(ResolvedHealthCheck.BuiltIn.StartupFastIntervalSeconds, merged.StartupFastIntervalSeconds);
        Assert.Equal(ResolvedHealthCheck.BuiltIn with { Port = 8080, StartupProbeEnabled = false }, merged);
    }
}
