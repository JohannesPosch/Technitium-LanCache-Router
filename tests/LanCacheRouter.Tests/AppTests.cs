using System.Net;
using DnsServerCore.ApplicationCommon;
using LanCacheRouter.Config;
using LanCacheRouter.DomainList;
using LanCacheRouter.Tests.Dns;
using LanCacheRouter.Tests.DomainList;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace LanCacheRouter.Tests;

public sealed class AppTests : IDisposable
{
    private readonly FakeDnsServer _server = new();
    private readonly FakeDomainSource _source = FakeDomainSource.WithSteamAndEpic();
    private readonly List<FakeDnsServer> _otherServers = [];
    private readonly App _app;

    public AppTests()
    {
        _app = new App(_ => _source);
    }

    public void Dispose()
    {
        _app.Dispose();

        foreach (FakeDnsServer server in _otherServers.Append(_server))
        {
            if (Directory.Exists(server.ApplicationFolder))
                Directory.Delete(server.ApplicationFolder, recursive: true);
        }
    }

    /// <summary>A second server object with its own app folder, for tests that need a misbehaving or unusual host.</summary>
    private FakeDnsServer OtherServer(Action<string>? onLog = null, bool withoutResponsiblePerson = false)
    {
        FakeDnsServer server = new() { OnLog = onLog, WithoutResponsiblePerson = withoutResponsiblePerson };
        _otherServers.Add(server);
        return server;
    }

    /// <summary>A working configuration: the "none" probe with one required success makes the server Healthy at once, so no network is needed.</summary>
    private static string Enabled(string extra = "") => $$"""
        {
          "enabled": true,
          "domainList": { "baseUrl": "{{FakeDomainSource.BaseUrl}}" },
          "cacheServers": [ { "name": "cache-1", "address": "192.168.17.44", "healthCheck": { "type": "none", "successThreshold": 1 } } ],
          "routes": [ { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "cache-1" } ]
          {{extra}}
        }
        """;

    private async Task<DnsDatagram?> Ask(string name = "cdn.steamcontent.com", string client = "192.168.17.50")
        => await _app.ProcessRequestAsync(TestDns.Query(name), TestDns.Client(client));

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);

        return condition();
    }

    /// <summary>Waits (in real time) until the app answers the query, i.e. the first probe and list download are done.</summary>
    private async Task<bool> WaitUntilAnswering(string name = "cdn.steamcontent.com")
    {
        for (int i = 0; i < 300; i++)
        {
            if (await Ask(name) is not null)
                return true;

            await Task.Delay(10);
        }

        return false;
    }

    [Fact]
    public void ExposesTheMetadataTechnitiumNeeds()
    {
        Assert.False(string.IsNullOrWhiteSpace(_app.Description));
        Assert.True(_app.Preference > 100, "must run after Advanced Blocking (default 100) so blocked domains stay blocked");
        Assert.IsAssignableFrom<IDnsApplication>(_app);
        Assert.IsAssignableFrom<IDnsRequestBlockingHandler>(_app);
        Assert.IsAssignableFrom<IDnsApplicationPreference>(_app);
        Assert.NotNull(Activator.CreateInstance(typeof(App))); // Technitium creates apps with the public parameterless constructor
    }

    [Fact]
    public async Task IsAllowedAsync_IsAlwaysFalse_SoOtherBlockingHandlersStillRun()
    {
        await _app.InitializeAsync(_server, Enabled());

        Assert.False(await _app.IsAllowedAsync(TestDns.Query("cdn.steamcontent.com"), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public async Task NoConfig_MeansInstalledButInactive()
    {
        await _app.InitializeAsync(_server, null);

        Assert.Null(await Ask());
        Assert.True(_server.LogContains("disabled"));
    }

    [Fact]
    public async Task ShippedExampleConfig_IsValid_AndInactive()
    {
        string shipped = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "shipped-dnsApp.config"));

        ConfigParseResult result = ConfigParser.Parse(shipped);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.False(result.Config!.Enabled);
        Assert.Empty(result.Warnings);
        await _app.InitializeAsync(_server, shipped); // and Technitium accepts it
    }

    [Fact]
    public async Task EnabledConfig_AnswersCacheDomainsForRoutedClients()
    {
        await _app.InitializeAsync(_server, Enabled());

        Assert.True(await WaitUntilAnswering());
        DnsDatagram response = (await Ask())!;
        Assert.Equal(IPAddress.Parse("192.168.17.44"), ((DnsARecordData)response.Answer.Single().RDATA).Address);
        Assert.Null(await Ask(client: "10.9.9.9")); // not routed
        Assert.Null(await Ask("www.example.com")); // not a cache domain
        Assert.True(_server.LogContains("LanCache Router: enabled with 1 cache server(s) and 1 route(s)"));
    }

    [Fact]
    public async Task InvalidConfig_Throws_ListsAllProblems_AndKeepsThePreviousConfigActive()
    {
        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => _app.InitializeAsync(
            _server, """{ "cacheServers": [ { "name": "x", "address": "nope" } ], "routes": [ { "priority": 1, "sourceCidr": "bad", "server": "x" } ] }"""));

        Assert.Contains("not a valid IPv4 address", error.Message);
        Assert.Contains("not a valid CIDR", error.Message);
        Assert.NotNull(await Ask()); // still serving with the previous configuration
        Assert.True(_server.LogContains("configuration rejected"));
    }

    [Fact]
    public async Task MalformedJson_IsRejectedLikeAnyOtherInvalidConfig()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _app.InitializeAsync(_server, "{ not json"));
    }

    [Fact]
    public async Task DisablingTheApp_StopsAnswering_AndReEnablingResumes()
    {
        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());

        await _app.InitializeAsync(_server, """{ "enabled": false }""");
        Assert.Null(await Ask());

        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());
    }

    [Fact]
    public async Task ChangingTheRoute_TakesEffectOnTheSameInstance()
    {
        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());

        await _app.InitializeAsync(_server, Enabled().Replace("192.168.17.0/24", "192.168.99.0/24"));

        Assert.Null(await Ask(client: "192.168.17.50"));
        Assert.NotNull(await Ask(client: "192.168.99.7"));
    }

    [Fact]
    public async Task ConfigWarnings_AreLogged()
    {
        string shadowed = Enabled().Replace("\"routes\": [", """
            "routes": [ { "priority": 5, "sourceCidr": "192.168.0.0/16", "server": "cache-1" },
            """);

        await _app.InitializeAsync(_server, shadowed);

        Assert.True(_server.LogContains("never used: shadowed"));
    }

    [Fact]
    public async Task DomainListIsPersistedInsideTheAppFolder()
    {
        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());

        Assert.True(File.Exists(Path.Combine(_server.ApplicationFolder, "state", "domains-snapshot.json")));
    }

    [Fact]
    public async Task Dispose_CanBeCalledAfterInitializeAndTwice()
    {
        await _app.InitializeAsync(_server, Enabled());

        _app.Dispose();
        _app.Dispose();
    }

    private static int FreePort()
    {
        System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static IPAddress AnswerAddress(DnsDatagram response) => ((DnsARecordData)response.Answer.Single().RDATA).Address;

    /// <summary>Primary is probed over TCP on the given port; the secondary always counts as healthy.</summary>
    private static string WithFailover(int primaryPort) => $$"""
        {
          "domainList": { "baseUrl": "{{FakeDomainSource.BaseUrl}}" },
          "cacheServers": [
            { "name": "primary",   "address": "127.0.0.1",    "healthCheck": { "type": "tcp", "port": {{primaryPort}}, "failThreshold": 1, "successThreshold": 1 } },
            { "name": "secondary", "address": "192.168.18.44", "healthCheck": { "type": "none", "successThreshold": 1 } }
          ],
          "routes": [ { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary", "failover": { "action": "failover", "server": "secondary" } } ]
        }
        """;

    [Fact]
    public async Task DeadPrimary_FailsOverToTheSecondary_AndNeverAnswersWithTheDeadServer()
    {
        await _app.InitializeAsync(_server, WithFailover(FreePort()));

        Assert.True(await WaitUntilAnswering());
        for (int i = 0; i < 20; i++)
            Assert.Equal(IPAddress.Parse("192.168.18.44"), AnswerAddress((await Ask())!));

        // The failover server answers as soon as the list is loaded, possibly before the first probe of the primary has finished.
        Assert.True(await WaitFor(() => _server.LogContains("'primary'") && _server.LogContains("-> Unhealthy")));
    }

    [Fact]
    public async Task LivePrimary_IsPreferred_AndFailsOverWhenItDies()
    {
        System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            await _app.InitializeAsync(_server, WithFailover(port));
            Assert.True(await WaitUntilAnswering());
            Assert.Equal(IPAddress.Loopback, AnswerAddress((await Ask())!));
        }
        finally
        {
            listener.Stop();
        }

        // The listener is gone: with a fast probe the primary becomes unhealthy and the secondary takes over.
        await _app.InitializeAsync(_server, WithFailover(port).Replace("\"failThreshold\": 1", "\"failThreshold\": 1, \"intervalSeconds\": 1, \"timeoutSeconds\": 1"));
        for (int i = 0; i < 300 && AnswerAddress((await Ask())!).Equals(IPAddress.Loopback); i++)
            await Task.Delay(20);

        Assert.Equal(IPAddress.Parse("192.168.18.44"), AnswerAddress((await Ask())!));
    }

    [Fact]
    public async Task ServerThatHasNotBeenProbedYet_IsNeverServed()
    {
        // A probe that cannot finish within the test keeps the server Unknown; Unknown must pass through (ADR 0003).
        string config = $$"""
            {
              "domainList": { "baseUrl": "{{FakeDomainSource.BaseUrl}}" },
              "cacheServers": [ { "name": "slow", "address": "192.0.2.1", "healthCheck": { "type": "tcp", "port": 9, "successThreshold": 1, "timeoutSeconds": 30, "intervalSeconds": 30, "startupProbe": { "enabled": false } } } ],
              "routes": [ { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "slow" } ]
            }
            """;

        await _app.InitializeAsync(_server, config);
        await Task.Delay(300);

        Assert.Null(await Ask());
    }

    [Fact]
    public async Task SavingTheSameConfigAgain_DoesNotInterruptService()
    {
        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());

        for (int i = 0; i < 5; i++)
        {
            await _app.InitializeAsync(_server, Enabled());
            Assert.NotNull(await Ask()); // no gap: health state and domain list survive the re-save
        }
    }

    [Fact]
    public async Task AaaaAndHttps_ForCacheDomains_AreNodata_SoDualStackClientsCannotBypassTheCache()
    {
        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());

        foreach (DnsResourceRecordType type in new[] { DnsResourceRecordType.AAAA, DnsResourceRecordType.HTTPS, DnsResourceRecordType.SVCB })
        {
            DnsDatagram response = (await _app.ProcessRequestAsync(TestDns.Query("cdn.steamcontent.com", type), TestDns.Client("192.168.17.50")))!;

            Assert.Equal(DnsResponseCode.NoError, response.RCODE);
            Assert.Empty(response.Answer);
            Assert.Single(response.Authority);
        }
    }

    [Fact]
    public async Task ServiceFilter_LimitsWhichDomainsAreAnswered()
    {
        string config = Enabled().Replace(
            $"\"baseUrl\": \"{FakeDomainSource.BaseUrl}\"",
            $"\"baseUrl\": \"{FakeDomainSource.BaseUrl}\", \"services\": {{ \"include\": [\"epicgames\"] }}");

        await _app.InitializeAsync(_server, config);

        Assert.True(await WaitUntilAnswering("epicgames-download1.akamaized.net"));
        Assert.Null(await Ask("cdn.steamcontent.com"));
    }

    [Fact]
    public async Task BeforeTheFirstInitialize_NothingIsAnswered()
    {
        Assert.Null(await Ask());
        Assert.False(await _app.IsAllowedAsync(TestDns.Query("cdn.steamcontent.com"), TestDns.Client("192.168.17.50")));
    }

    [Fact]
    public async Task FirstConfigurationInvalid_IsRejected_AndTheAppStaysInactive()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _app.InitializeAsync(_server, """{ "routes": [ { "priority": 1, "sourceCidr": "192.168.17.0/24", "server": "ghost" } ] }"""));

        Assert.Null(await Ask());
        Assert.True(_server.LogContains("configuration rejected"));
        Assert.False(_server.LogContains("enabled with"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task BlankConfiguration_MeansInstalledButInactive(string config)
    {
        await _app.InitializeAsync(_server, config);

        Assert.Null(await Ask());
        Assert.True(_server.LogContains("disabled"));
    }

    [Fact]
    public async Task RejectedSave_IsNeverPartiallyApplied()
    {
        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());
        int logLinesBefore = _server.Logs.Count;

        // Valid routes, but the server list contains a typo that fails validation.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _app.InitializeAsync(_server,
            Enabled().Replace("192.168.17.44", "192.168.17.4x").Replace("192.168.17.0/24", "192.168.99.0/24")));

        Assert.NotNull(await Ask(client: "192.168.17.50")); // the old route still applies
        Assert.Null(await Ask(client: "192.168.99.7")); // the new one does not
        Assert.DoesNotContain(_server.Logs.Skip(logLinesBefore), l => l.Contains("enabled with"));
    }

    [Fact]
    public async Task DisabledApp_SendsNoProbes_AndEnablingItAgainResumesProbesAndTheDownload()
    {
        System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int connections = 0;
        Task acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using System.Net.Sockets.TcpClient _ = await listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref connections);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                // listener stopped by the test
            }
        });

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string config = $$"""
                {
                  "domainList": { "baseUrl": "{{FakeDomainSource.BaseUrl}}" },
                  "cacheServers": [ { "name": "local", "address": "127.0.0.1", "healthCheck": { "type": "tcp", "port": {{port}}, "intervalSeconds": 1, "timeoutSeconds": 1, "successThreshold": 1 } } ],
                  "routes": [ { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "local" } ]
                }
                """;

            await _app.InitializeAsync(_server, config);
            Assert.True(await WaitUntilAnswering());
            for (int i = 0; i < 300 && Volatile.Read(ref connections) < 3; i++)
                await Task.Delay(10);
            Assert.True(Volatile.Read(ref connections) >= 3, "the server should be probed every second while enabled");

            await _app.InitializeAsync(_server, """{ "enabled": false }""");
            await Task.Delay(300);
            int whileDisabled = Volatile.Read(ref connections);
            int downloads = _source.IndexCalls;
            await Task.Delay(2300); // more than two probe intervals

            Assert.Equal(whileDisabled, Volatile.Read(ref connections));
            Assert.Equal(downloads, _source.IndexCalls);
            Assert.Null(await Ask());

            await _app.InitializeAsync(_server, config);
            for (int i = 0; i < 300 && Volatile.Read(ref connections) <= whileDisabled; i++)
                await Task.Delay(10);

            Assert.True(Volatile.Read(ref connections) > whileDisabled, "probing must resume");
            Assert.True(await WaitFor(() => _source.IndexCalls == downloads + 1)); // enabling counts as a start: the list is refreshed
            Assert.True(await WaitUntilAnswering());
        }
        finally
        {
            listener.Stop();
            await acceptLoop;
        }
    }

    [Fact]
    public async Task ListSourceDown_AtStartup_PassesEverythingThrough_AndNeverThrows()
    {
        _source.FailAll = true;

        await _app.InitializeAsync(_server, Enabled());
        await Task.Delay(300);

        Assert.Null(await Ask());
        Assert.Null(await Ask("www.example.com"));
        Assert.True(_server.LogContains("refresh failed"));
        Assert.True(_server.LogContains("enabled with 1 cache server(s)"));
    }

    private static void PlantSnapshot(FakeDnsServer server, params string[] patterns)
        => new DomainSnapshotStore(Path.Combine(server.ApplicationFolder, "state", "domains-snapshot.json"), _ => { })
            .Save(new RawDomainList(DateTimeOffset.UtcNow.AddDays(-1), FakeDomainSource.BaseUrl, [new ServiceDomains("remembered", patterns)]));

    [Fact]
    public async Task SnapshotFromTheLastRun_KeepsCachingWorking_WhileTheSourceIsDown()
    {
        PlantSnapshot(_server, "*.remembered.example");
        _source.FailAll = true;

        await _app.InitializeAsync(_server, Enabled());

        Assert.True(await WaitUntilAnswering("cdn.remembered.example"));
        Assert.Null(await Ask("cdn.steamcontent.com")); // not in the old snapshot, and the download keeps failing
        Assert.True(_server.LogContains("loaded persisted snapshot"));
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{ "fetchedAt": "2026-01-01T00:00:00+00:00", "baseUrl": "https://example.test/cache-domains/master/", "services": [ null ] }""")]
    [InlineData("")]
    public async Task DamagedSnapshotFromTheLastRun_IsIgnored_AndTheListIsDownloaded(string content)
    {
        string path = Path.Combine(_server.ApplicationFolder, "state", "domains-snapshot.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);

        await _app.InitializeAsync(_server, Enabled());

        Assert.True(await WaitUntilAnswering());
    }

    [Fact]
    public async Task ALoggerThatThrows_NeverBreaksConfigurationProbesOrAnswers()
    {
        FakeDnsServer broken = OtherServer(onLog: _ => throw new IOException("log disk full"));

        await _app.InitializeAsync(broken, Enabled(", \"logging\": { \"queries\": true }"));

        Assert.True(await WaitUntilAnswering());
        Assert.NotNull(await Ask());
        await _app.InitializeAsync(broken, Enabled()); // and a re-save works as well
        Assert.NotNull(await Ask());
    }

    [Fact]
    public async Task ConfigurationSavedWhileQueriesRun_NeverBreaksAQuery()
    {
        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());
        using CancellationTokenSource stop = new();
        int answered = 0, passedThrough = 0;

        Task[] clients = [.. Enumerable.Range(0, 4).Select(c => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                DnsDatagram? response = await Ask(client: "192.168.17." + (c + 10)); // must never throw
                if (response is null)
                    Interlocked.Increment(ref passedThrough);
                else
                    Interlocked.Increment(ref answered);
            }
        }))];

        for (int i = 0; i < 40; i++)
        {
            await _app.InitializeAsync(_server, i % 4 == 3 ? """{ "enabled": false }""" : Enabled(i % 2 == 0 ? ", \"dns\": { \"ttl\": 30 }" : ""));
            await Task.Delay(5);
        }

        await _app.InitializeAsync(_server, Enabled());
        stop.Cancel();
        await Task.WhenAll(clients);

        Assert.True(answered > 0 && passedThrough > 0);
        Assert.False(_server.LogContains("Error handling query"));
        Assert.True(await WaitUntilAnswering());
    }

    [Fact]
    public async Task ManySimultaneousSaves_LeaveOneConsistentConfiguration()
    {
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => _app.InitializeAsync(_server, i % 2 == 0 ? Enabled() : Enabled().Replace("192.168.17.0/24", "192.168.55.0/24")))));
        await _app.InitializeAsync(_server, Enabled());

        Assert.True(await WaitUntilAnswering());
        Assert.Null(await Ask(client: "192.168.55.7"));
    }

    [Fact]
    public async Task QueryLogging_FollowsTheConfiguration()
    {
        await _app.InitializeAsync(_server, Enabled(", \"logging\": { \"queries\": true }"));
        Assert.True(await WaitUntilAnswering());

        Assert.True(_server.LogContains("LanCache Router: Answered 192.168.17.50 A cdn.steamcontent.com [steam] -> cache-1 (192.168.17.44)"));

        await _app.InitializeAsync(_server, Enabled());
        int lines = _server.Logs.Count(l => l.Contains("Answered"));
        await Ask();
        Assert.Equal(lines, _server.Logs.Count(l => l.Contains("Answered")));
    }

    [Fact]
    public async Task DisablingTheServer_StopsServingItAtOnce_WithoutWaitingForAnything()
    {
        await _app.InitializeAsync(_server, Enabled());
        Assert.True(await WaitUntilAnswering());

        await _app.InitializeAsync(_server, Enabled().Replace("\"name\": \"cache-1\",", "\"name\": \"cache-1\", \"enabled\": false,"));

        Assert.Null(await Ask()); // takes effect on the very next query
    }

    [Fact]
    public async Task AnswerTtl_FollowsTheConfiguration()
    {
        await _app.InitializeAsync(_server, Enabled(", \"dns\": { \"ttl\": 5 }"));
        Assert.True(await WaitUntilAnswering());

        Assert.Equal(5u, (await Ask())!.Answer[0].TTL);

        await _app.InitializeAsync(_server, Enabled(", \"dns\": { \"ttl\": 300 }"));
        Assert.Equal(300u, (await Ask())!.Answer[0].TTL);
    }

    [Fact]
    public async Task DisabledPrimary_WithAFailover_ServesTheFailoverAtOnce()
    {
        string config = $$"""
            {
              "domainList": { "baseUrl": "{{FakeDomainSource.BaseUrl}}" },
              "cacheServers": [
                { "name": "primary",   "address": "192.168.17.44", "enabled": false, "healthCheck": { "type": "none" } },
                { "name": "secondary", "address": "192.168.18.44", "healthCheck": { "type": "none" } }
              ],
              "routes": [ { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "primary", "failover": { "action": "failover", "server": "secondary" } } ]
            }
            """;

        await _app.InitializeAsync(_server, config);

        Assert.True(await WaitUntilAnswering());
        Assert.Equal(IPAddress.Parse("192.168.18.44"), AnswerAddress((await Ask())!));
    }

    [Fact]
    public async Task ServerTypeNone_IsServedFromTheFirstQuery_WithoutWaitingForProbes()
    {
        string config = $$"""
            {
              "domainList": { "baseUrl": "{{FakeDomainSource.BaseUrl}}", "refreshOnStartup": false },
              "cacheServers": [ { "name": "static", "address": "192.168.17.44", "healthCheck": { "type": "none", "successThreshold": 50 } } ],
              "routes": [ { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "static" } ]
            }
            """;
        PlantSnapshot(_server, "*.steamcontent.com");

        await _app.InitializeAsync(_server, config);

        Assert.NotNull(await Ask()); // synchronously after InitializeAsync: no thresholds to wait for, the list is the snapshot
    }

    [WireFormatTheory]
    [InlineData(DnsResourceRecordType.AAAA)]
    [InlineData(DnsResourceRecordType.HTTPS)]
    public async Task WithoutAResponsiblePerson_TheNegativeAnswerStillHasAValidSoa(DnsResourceRecordType type)
    {
        FakeDnsServer server = OtherServer(withoutResponsiblePerson: true);
        await _app.InitializeAsync(server, Enabled());
        Assert.True(await WaitUntilAnswering());

        DnsDatagram response = (await _app.ProcessRequestAsync(TestDns.Query("cdn.steamcontent.com", type), TestDns.Client("192.168.17.50")))!;
        DnsDatagram onTheWire = TestDns.RoundTrip(response);

        DnsSOARecordData soa = (DnsSOARecordData)onTheWire.Authority.Single().RDATA;
        Assert.Equal("dns01.example.test", soa.PrimaryNameServer);
        Assert.StartsWith("hostmaster", soa.ResponsiblePerson);
        Assert.EndsWith("dns01.example.test", soa.ResponsiblePerson);
    }

    [Fact]
    public async Task ConfigWarnings_AboutIgnoredSettings_AreLogged()
    {
        string config = Enabled().Replace("\"server\": \"cache-1\" }", "\"server\": \"cache-1\", \"behavior\": \"passthrough\" }");

        await _app.InitializeAsync(_server, config);

        Assert.True(_server.LogContains("LanCache Router: warning: routes[priority=10]: server is ignored because behavior is passthrough"));
    }
}
