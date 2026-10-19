using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LanCacheRouter.Config;
using LanCacheRouter.Health.Probes;

namespace LanCacheRouter.Tests.Health;

public class ProbeTests
{
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    private static int FreePort()
    {
        TcpListener listener = new(Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ResolvedHealthCheck Check(int port, params int[] expectedStatus)
        => ResolvedHealthCheck.BuiltIn with { Port = port, ExpectedStatus = expectedStatus.Length == 0 ? [200, 204] : expectedStatus };

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    [Fact]
    public async Task Tcp_Succeeds_WhenSomethingListens()
    {
        TcpListener listener = new(Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            ProbeResult result = await new TcpProbe().ProbeAsync(Loopback, Check(port), Timeout());

            Assert.True(result.Success);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Tcp_Fails_WhenPortIsClosed()
    {
        ProbeResult result = await new TcpProbe().ProbeAsync(Loopback, Check(FreePort()), Timeout());

        Assert.False(result.Success);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public async Task Tcp_Throws_WhenCancelled()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TcpProbe().ProbeAsync(Loopback, Check(FreePort()), cancelled.Token));
    }

    /// <summary>
    /// Starts a local HTTP server answering every request with the given status, and returns the port and the path it saw.
    /// </summary>
    private static (int Port, HttpListener Listener, Task<string> SeenPath) ServeOnce(int statusCode)
    {
        int port = FreePort();
        HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        Task<string> seen = Task.Run(async () =>
        {
            HttpListenerContext context = await listener.GetContextAsync();
            context.Response.StatusCode = statusCode;
            context.Response.Close();
            return context.Request.Url!.AbsolutePath;
        });

        return (port, listener, seen);
    }

    [Theory]
    [InlineData(204, true)]
    [InlineData(200, true)]
    [InlineData(500, false)]
    [InlineData(404, false)]
    public async Task Http_ChecksStatusAgainstExpectedList(int status, bool expected)
    {
        (int port, HttpListener listener, Task<string> seenPath) = ServeOnce(status);
        try
        {
            ProbeResult result = await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(port), Timeout());

            Assert.Equal(expected, result.Success);
            Assert.Equal($"HTTP {status}", result.Detail);
            Assert.Equal("/lancache-heartbeat", await seenPath);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task Http_UsesConfiguredExpectedStatus()
    {
        (int port, HttpListener listener, _) = ServeOnce(500);
        try
        {
            ProbeResult result = await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(port, 500), Timeout());

            Assert.True(result.Success);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task Http_RequestsTheConfiguredPath()
    {
        (int port, HttpListener listener, Task<string> seenPath) = ServeOnce(200);
        try
        {
            await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(port) with { Path = "/health/ready" }, Timeout());

            Assert.Equal("/health/ready", await seenPath);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task Http_DoesNotFollowRedirects_SoARedirectIsNotHealthy()
    {
        (int port, HttpListener listener, _) = ServeOnce(302);
        try
        {
            ProbeResult result = await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(port), Timeout());

            Assert.False(result.Success);
            Assert.Equal("HTTP 302", result.Detail);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task Http_Fails_WhenNothingListens()
    {
        ProbeResult result = await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(FreePort()), Timeout());

        Assert.False(result.Success);
    }

    [Fact]
    public async Task NoneProbe_AlwaysSucceeds()
    {
        ProbeResult result = await ProbeFactory.Create(ProbeType.None).ProbeAsync(Loopback, Check(FreePort()), Timeout());

        Assert.True(result.Success);
    }

    [Theory]
    [InlineData(ProbeType.Tcp, typeof(TcpProbe))]
    [InlineData(ProbeType.Http, typeof(HttpProbe))]
    [InlineData(ProbeType.Https, typeof(HttpProbe))]
    public void Factory_ReturnsMatchingImplementation(ProbeType type, Type expected)
    {
        Assert.IsType(expected, ProbeFactory.Create(type));
    }

    [Fact]
    public void Factory_RejectsAnUnknownType_AndSharesOneInstancePerType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeFactory.Create((ProbeType)99));
        Assert.Same(ProbeFactory.Create(ProbeType.Tcp), ProbeFactory.Create(ProbeType.Tcp));
        Assert.NotSame(ProbeFactory.Create(ProbeType.Http), ProbeFactory.Create(ProbeType.Https));
    }

    [Fact]
    public async Task NoneProbe_NeverTouchesTheNetwork()
    {
        ProbeResult result = await ProbeFactory.Create(ProbeType.None).ProbeAsync(IPAddress.Parse("192.0.2.1"), ResolvedHealthCheck.BuiltIn, new CancellationToken(canceled: true));

        Assert.True(result.Success);
    }

    /// <summary>
    /// A server that hands every TCP connection to the test. It keeps accepting, because HttpClient retries a request once
    /// on a new connection when the first one is dropped before any response; <c>Handled</c> completes with the first one.
    /// </summary>
    private static (int Port, TcpListener Listener, Task Handled) ServeRaw(Func<TcpClient, Task> handler)
    {
        TcpListener listener = new(Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();
                    try
                    {
                        await handler(client);
                    }
                    finally
                    {
                        first.TrySetResult();
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException or IOException)
            {
                // the probe went away or the test stopped the listener
            }
        });

        return (port, listener, first.Task);
    }

    private static async Task<string> ReadRequest(Stream stream)
    {
        byte[] buffer = new byte[4096];
        int read = await stream.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static byte[] Response(int status) => Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    [Fact]
    public async Task Http_SendsAPlainGet_WithTheDocumentedUserAgentAndTheProbedAddressAsHost()
    {
        string request = "";
        (int port, TcpListener listener, Task handled) = ServeRaw(async client =>
        {
            NetworkStream stream = client.GetStream();
            request = await ReadRequest(stream);
            await stream.WriteAsync(Response(204));
        });
        try
        {
            ProbeResult result = await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(port) with { Path = "/lancache-heartbeat?probe=1" }, Timeout());
            await handled;

            Assert.True(result.Success);
            Assert.StartsWith("GET /lancache-heartbeat?probe=1 HTTP/1.1\r\n", request);
            Assert.Contains("User-Agent: LanCacheRouter-HealthCheck", request);
            Assert.Contains($"Host: 127.0.0.1:{port}", request);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Http_Fails_WhenTheServerAnswersWithGarbage()
    {
        (int port, TcpListener listener, _) = ServeRaw(async client =>
        {
            NetworkStream stream = client.GetStream();
            await ReadRequest(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("\0\0 this is not http \xff\xfe\r\n\r\n"));
        });
        try
        {
            ProbeResult result = await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(port), Timeout());

            Assert.False(result.Success);
            Assert.NotEmpty(result.Detail);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Http_Fails_WhenTheServerClosesWithoutAnswering()
    {
        (int port, TcpListener listener, _) = ServeRaw(async client =>
        {
            await ReadRequest(client.GetStream());
            client.Close();
        });
        try
        {
            Assert.False((await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(port), Timeout())).Success);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Http_Fails_WhenTheConnectionIsResetAtOnce()
    {
        // A zero linger time makes closing send a TCP reset instead of a normal shutdown.
        (int httpPort, TcpListener httpListener, _) = ServeRaw(client =>
        {
            client.LingerState = new LingerOption(true, 0);
            client.Close();
            return Task.CompletedTask;
        });
        try
        {
            Assert.False((await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(httpPort), Timeout())).Success);
        }
        finally
        {
            httpListener.Stop();
        }
    }

    [Fact]
    public async Task Tcp_Succeeds_WhenTheServerAcceptsAndImmediatelyHangsUp()
    {
        (int port, TcpListener listener, _) = ServeRaw(client =>
        {
            client.Close();
            return Task.CompletedTask;
        });
        try
        {
            Assert.True((await new TcpProbe().ProbeAsync(Loopback, Check(port), Timeout())).Success); // connecting is all that is checked
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Http_PropagatesCancellation_WhenTheServerNeverAnswers()
    {
        // The monitor relies on this: a hung probe must end with OperationCanceledException when its timeout expires.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (int port, TcpListener listener, _) = ServeRaw(async client =>
        {
            await ReadRequest(client.GetStream());
            await release.Task;
        });
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(port), timeout.Token));
        }
        finally
        {
            release.SetResult();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Tcp_ToANonRoutableAddress_NeverSucceeds()
    {
        // A non-routable address (RFC 5737 TEST-NET-1): the connect either hangs until cancelled or fails at once; never succeeds.
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(300));

        try
        {
            ProbeResult result = await new TcpProbe().ProbeAsync(IPAddress.Parse("192.0.2.1"), Check(80), timeout.Token);

            Assert.False(result.Success);
        }
        catch (OperationCanceledException)
        {
            // expected when the network silently drops the packets
        }
    }

    private static X509Certificate2 SelfSignedCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=not-the-address.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null);
    }

    private static (int Port, TcpListener Listener, Task<string> Request) ServeTls(int status)
    {
        X509Certificate2 certificate = SelfSignedCertificate();
        TcpListener listener = new(Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<string> request = Task.Run(async () =>
        {
            string seen = "";

            try
            {
                while (seen.Length == 0)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();

                    try
                    {
                        await using SslStream tls = new(client.GetStream());
                        await tls.AuthenticateAsServerAsync(certificate);
                        seen = await ReadRequest(tls);
                        await tls.WriteAsync(Response(status));
                        await tls.FlushAsync();
                    }
                    catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
                    {
                        // the client did not speak TLS; wait for the next connection
                    }
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // the test stopped the listener
            }

            return seen;
        });

        return (port, listener, request);
    }

    [Theory]
    [InlineData(204, true)]
    [InlineData(200, true)]
    [InlineData(503, false)]
    public async Task Https_AcceptsACertificateThatIsSelfSignedAndForAnotherName_AndChecksTheStatus(int status, bool expected)
    {
        (int port, TcpListener listener, Task<string> request) = ServeTls(status);
        try
        {
            ProbeResult result = await new HttpProbe(useTls: true).ProbeAsync(Loopback, Check(port), Timeout());

            Assert.Equal(expected, result.Success);
            Assert.Equal($"HTTP {status}", result.Detail);
            Assert.StartsWith("GET /lancache-heartbeat HTTP/1.1", await request);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Https_Fails_WhenThePortSpeaksPlainHttp_InsteadOfThrowing()
    {
        (int port, HttpListener listener, _) = ServeOnce(204);
        try
        {
            ProbeResult result = await new HttpProbe(useTls: true).ProbeAsync(Loopback, Check(port), Timeout());

            Assert.False(result.Success);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task Http_Fails_WhenThePortSpeaksTls()
    {
        (int port, TcpListener listener, Task<string> request) = ServeTls(204);
        try
        {
            ProbeResult result = await new HttpProbe(useTls: false).ProbeAsync(Loopback, Check(port), Timeout());

            Assert.False(result.Success);
        }
        finally
        {
            listener.Stop();
            Assert.Equal("", await request);
        }
    }

    private sealed class ThrowingHandler(Func<Exception> exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw exception();
    }

    public static TheoryData<string> RawNetworkFailures() => new() { "socket", "io", "http", "http-with-inner" };

    [Theory]
    [MemberData(nameof(RawNetworkFailures))]
    public async Task Http_ReturnsAFailure_ForEveryKindOfNetworkError_InsteadOfThrowing(string kind)
    {
        // .NET surfaces some network errors (for example a connection reset right after accept) as raw SocketException or IOException.
        Func<Exception> failure = kind switch
        {
            "socket" => () => new SocketException((int)SocketError.NotConnected),
            "io" => () => new IOException("The response ended prematurely."),
            "http" => () => new HttpRequestException("Connection refused"),
            _ => () => new HttpRequestException("outer", new SocketException((int)SocketError.ConnectionRefused)),
        };
        HttpProbe probe = new(useTls: false, new HttpClient(new ThrowingHandler(failure)));

        ProbeResult result = await probe.ProbeAsync(Loopback, Check(80), Timeout());

        Assert.False(result.Success);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public async Task Http_DoesNotSwallowCancellation()
    {
        HttpProbe probe = new(useTls: false, new HttpClient(new ThrowingHandler(() => new OperationCanceledException())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(Loopback, Check(80), Timeout()));
    }

    [Fact]
    public async Task Http_ReportsTheSocketErrorByName_ForARawSocketException()
    {
        HttpProbe probe = new(useTls: false, new HttpClient(new ThrowingHandler(() => new SocketException((int)SocketError.NotConnected))));

        ProbeResult result = await probe.ProbeAsync(Loopback, Check(80), Timeout());

        Assert.Equal("NotConnected", result.Detail);
    }
}
