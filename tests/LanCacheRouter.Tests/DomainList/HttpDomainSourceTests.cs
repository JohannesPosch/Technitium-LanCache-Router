using System.Net;
using System.Net.Sockets;
using System.Text;
using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests.DomainList;

public class HttpDomainSourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

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

        return (new Uri($"http://127.0.0.1:{port}/file.txt"), listener);
    }

    private static HttpDomainSource Source(long maxBytes = HttpDomainSource.DefaultMaxBytes)
        => new(new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan }, maxBytes);

    [Fact]
    public async Task ReturnsTheBodyAsText()
    {
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            byte[] body = Encoding.UTF8.GetBytes("*.steamcontent.com\nümlaut.example\n");
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });
        try
        {
            Assert.Equal("*.steamcontent.com\nümlaut.example\n", await Source().GetStringAsync(url, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task NotFound_ThrowsHttpRequestException()
    {
        (Uri url, HttpListener listener) = Serve(context =>
        {
            context.Response.StatusCode = 404;
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
    public async Task BodyOverTheLimit_IsRejected()
    {
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            context.Response.SendChunked = true; // no Content-Length, so the limit must be enforced while reading
            await context.Response.OutputStream.WriteAsync(new byte[2000]);
            context.Response.Close();
        });
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Source(maxBytes: 1000).GetStringAsync(url, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task DeclaredContentLengthOverTheLimit_IsRejected()
    {
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            await context.Response.OutputStream.WriteAsync(new byte[2000]);
            context.Response.Close();
        });
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Source(maxBytes: 1000).GetStringAsync(url, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task SlowServer_ThrowsTimeoutException()
    {
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            await Task.Delay(3000);
            context.Response.Close();
        });
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => Source().GetStringAsync(url, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsTimeout()
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
    public async Task AByteOrderMark_IsNotPartOfTheText()
    {
        // Files saved on Windows, for example on a self-hosted mirror, often start with one; it would break the JSON index
        // and silently invalidate the first domain of every list.
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            byte[] body = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{ \"cache_domains\": [] }")];
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });
        try
        {
            Assert.Equal("{ \"cache_domains\": [] }", await Source().GetStringAsync(url, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task EmptyBody_IsAnEmptyString()
    {
        (Uri url, HttpListener listener) = Serve(context =>
        {
            context.Response.Close();
            return Task.CompletedTask;
        });
        try
        {
            Assert.Equal("", await Source().GetStringAsync(url, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task DeclaredContentLengthOverTheLimit_IsRejectedWithoutWaitingForTheBody()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            context.Response.ContentLength64 = 10_000_000; // announces far more than the limit...
            await context.Response.OutputStream.WriteAsync(new byte[10]);
            await context.Response.OutputStream.FlushAsync();
            await release.Task; // ...and then never delivers it
            context.Response.Abort();
        });
        try
        {
            HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => Source(maxBytes: 1000).GetStringAsync(url, TimeSpan.FromSeconds(10), CancellationToken.None));

            Assert.Contains("larger than", error.Message);
        }
        finally
        {
            release.SetResult();
            listener.Close();
        }
    }

    [Fact]
    public async Task BodyExactlyAtTheLimit_IsAccepted()
    {
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes(new string('a', 1000)));
            context.Response.Close();
        });
        try
        {
            Assert.Equal(1000, (await Source(maxBytes: 1000).GetStringAsync(url, Timeout, CancellationToken.None)).Length);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task ServerThatClosesTheConnectionMidBody_FailsInsteadOfReturningATruncatedList()
    {
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            context.Response.ContentLength64 = 1000;
            await context.Response.OutputStream.WriteAsync(new byte[10]);
            await context.Response.OutputStream.FlushAsync();
            context.Response.Abort(); // connection dropped after 10 of 1000 bytes
        });
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Source().GetStringAsync(url, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    private static Func<HttpListenerContext, Task> RedirectFrom(string fromPath, string location)
        => async context =>
        {
            if (context.Request.Url!.AbsolutePath == fromPath)
            {
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = location;
            }
            else
            {
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("moved"));
            }

            context.Response.Close();
        };

    [Fact]
    public async Task ConfinedRedirect_StayingInsideTheFolder_IsFollowed()
    {
        (Uri url, HttpListener listener) = Serve(RedirectFrom("/ok/a.txt", "/ok/sub/b.txt"));
        try
        {
            Assert.Equal("moved", await Source().GetStringAsync(new Uri(url, "/ok/a.txt"), new Uri(url, "/ok/"), Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task ConfinedRedirect_ToAnotherPathOnTheSameHost_IsRejected()
    {
        (Uri url, HttpListener listener) = Serve(RedirectFrom("/ok/a.txt", "/elsewhere/b.txt"));
        try
        {
            HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(
                () => Source().GetStringAsync(new Uri(url, "/ok/a.txt"), new Uri(url, "/ok/"), Timeout, CancellationToken.None));

            Assert.Contains("redirected", error.Message);
            Assert.Contains("/elsewhere/b.txt", error.Message);
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task ConfinedRedirect_ToAnotherHost_IsRejected()
    {
        // "localhost" and "127.0.0.1" reach the same listener, but they are different hosts as far as the rule is concerned.
        (Uri url, HttpListener listener) = Serve(context =>
        {
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = $"http://localhost:{context.Request.LocalEndPoint.Port}/ok/b.txt";
            context.Response.Close();
            return Task.CompletedTask;
        });
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(
                () => Source().GetStringAsync(new Uri(url, "/ok/a.txt"), new Uri(url, "/ok/"), Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task ConfinedRequest_WithoutRedirect_Works()
    {
        (Uri url, HttpListener listener) = Serve(RedirectFrom("/never", "/never"));
        try
        {
            Assert.Equal("moved", await Source().GetStringAsync(new Uri(url, "/ok/a.txt"), new Uri(url, "/ok/"), Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task UnconfinedRedirect_StillGoesAnywhere()
    {
        (Uri url, HttpListener listener) = Serve(RedirectFrom("/ok/a.txt", "/elsewhere/b.txt"));
        try
        {
            Assert.Equal("moved", await Source().GetStringAsync(new Uri(url, "/ok/a.txt"), confinedTo: null, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }

    [Fact]
    public async Task Redirect_IsFollowed_ByTheDefaultClient()
    {
        // raw.githubusercontent.com does not redirect, but a self-hosted mirror may (http -> https, trailing slash, ...).
        (Uri url, HttpListener listener) = Serve(async context =>
        {
            if (context.Request.Url!.AbsolutePath == "/file.txt")
            {
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = "/moved.txt";
            }
            else
            {
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("moved"));
            }

            context.Response.Close();
        });
        try
        {
            Assert.Equal("moved", await Source().GetStringAsync(url, Timeout, CancellationToken.None));
        }
        finally
        {
            listener.Close();
        }
    }
}
