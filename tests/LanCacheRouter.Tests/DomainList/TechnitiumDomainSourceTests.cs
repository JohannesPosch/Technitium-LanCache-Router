using System.Net;
using System.Net.Sockets;
using System.Text;
using LanCacheRouter.DomainList;
using LanCacheRouter.Tests.Dns;

namespace LanCacheRouter.Tests.DomainList;

/// <summary>
/// The production download path: Technitium's own HTTP stack (<c>HttpClientNetworkHandler</c>), driven against a local server.
/// Everything else in the domain list is tested with fake sources, so this is the only place that proves the real wiring works.
/// </summary>
public class TechnitiumDomainSourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static (Uri Url, HttpListener Listener) Serve(Func<HttpListenerContext, Task> handler)
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                    await handler(await listener.GetContextAsync());
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // listener closed by the test
            }
        });

        return (new Uri($"http://127.0.0.1:{port}/cache_domains.json"), listener);
    }

    private static TechnitiumDomainSource Source(FakeDnsServer? server = null)
    {
        FakeDnsServer dns = server ?? new FakeDnsServer();
        return new TechnitiumDomainSource(() => dns);
    }

    [Fact]
    public async Task DownloadsTextThroughTechnitiumsHttpStack_AndIdentifiesItself()
    {
        string? userAgent = null;
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            userAgent = context.Request.UserAgent;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("*.steamcontent.com\n"));
            context.Response.Close();
        });
        try
        {
            string text = await Source().GetStringAsync(url, Timeout, CancellationToken.None);

            Assert.Equal("*.steamcontent.com\n", text);
            Assert.Equal("Technitium DNS Server LanCacheRouter", userAgent);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task ErrorStatus_IsAnHttpRequestException()
    {
        (Uri url, HttpListener listener) = Serve(context =>
        {
            context.Response.StatusCode = 503;
            context.Response.Close();
            return Task.CompletedTask;
        });
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Source().GetStringAsync(url, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task SlowServer_IsATimeoutException()
    {
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            await Task.Delay(3000);
            context.Response.Close();
        });
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => Source().GetStringAsync(url, TimeSpan.FromMilliseconds(300), CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task ServerThatIsNotListening_FailsWithoutHanging()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await Assert.ThrowsAnyAsync<Exception>(() => Source().GetStringAsync(new Uri($"http://127.0.0.1:{port}/x"), Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task ABodyLargerThanTheLimit_IsRejected()
    {
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            context.Response.SendChunked = true;
            byte[] chunk = new byte[64 * 1024];
            try
            {
                for (int i = 0; i < 100; i++) // 6.4 MB, over the 5 MB limit
                    await context.Response.OutputStream.WriteAsync(chunk);

                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                // the client gave up after the limit, as intended
            }
        });
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Source().GetStringAsync(url, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task CallerCancellation_IsPassedOn()
    {
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            await Task.Delay(3000);
            context.Response.Close();
        });
        using CancellationTokenSource cancel = new();
        try
        {
            Task<string> request = Source().GetStringAsync(url, Timeout, cancel.Token);
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task WorksForTheWholeFetchPipeline_IncludingTheIndexAndAFile()
    {
        (Uri baseUrl, HttpListener listener) = Serve(async context =>
        {
            string path = context.Request.Url!.AbsolutePath;
            string body = path.EndsWith("cache_domains.json")
                ? """{ "cache_domains": [ { "name": "steam", "domain_files": ["steam.txt"] } ] }"""
                : path.EndsWith("steam.txt") ? "# steam\n*.steamcontent.com\nlancache.steamcontent.com\n" : "";
            context.Response.StatusCode = body.Length == 0 ? 404 : 200;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
            context.Response.Close();
        });
        try
        {
            string root = new Uri(baseUrl, "./").ToString();

            RawDomainList list = await DomainListFetcher.FetchAsync(root, Timeout, Source(), _ => { }, DateTimeOffset.UnixEpoch, CancellationToken.None);

            Assert.Equal(["*.steamcontent.com", "lancache.steamcontent.com"], list.Services.Single().Patterns);
        }
        finally
        {
            listener.Close();
        }
    }
}
