using System.Net;
using System.Net.Sockets;
using LanCacheRouter.Config;

namespace LanCacheRouter.Health.Probes;

/// <summary>
/// Sends an HTTP or HTTPS GET to the address and succeeds if the status code is one of the expected ones
/// (the script's <c>PROBE_MODE=http</c>). The lancache monolithic container answers <c>/lancache-heartbeat</c> with 204.
/// </summary>
public sealed class HttpProbe : IProbe
{
    /// <summary>
    /// Shared client. Redirects are not followed, and TLS certificates are not validated: the probe addresses the
    /// server by IP, so a certificate could never match, and only liveness is checked, never content.
    /// </summary>
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "LanCacheRouter-HealthCheck" } }
    };

    private readonly bool _useTls;
    private readonly HttpClient _client;

    /// <summary>
    /// Creates a probe for HTTP or HTTPS.
    /// </summary>
    /// <param name="useTls"><c>true</c> to probe with HTTPS.</param>
    public HttpProbe(bool useTls) : this(useTls, Client)
    {
    }

    /// <summary>
    /// Creates a probe that sends its requests through the given client. Used by tests to provoke failures that are hard to cause with a real server.
    /// </summary>
    /// <param name="useTls"><c>true</c> to probe with HTTPS.</param>
    /// <param name="client">The client to send requests with; it must not follow redirects.</param>
    internal HttpProbe(bool useTls, HttpClient client)
    {
        _useTls = useTls;
        _client = client;
    }

    /// <inheritdoc/>
    public async Task<ProbeResult> ProbeAsync(IPAddress address, ResolvedHealthCheck check, CancellationToken cancellationToken)
    {
        string host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        string url = $"{(_useTls ? "https" : "http")}://{host}:{check.Port}{check.Path}";

        try
        {
            using HttpResponseMessage response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            int status = (int)response.StatusCode;
            return new ProbeResult(check.ExpectedStatus.Contains(status), $"HTTP {status}");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult(false, ex.InnerException?.Message ?? ex.Message);
        }
        catch (SocketException ex)
        {
            // A connection that is reset right after it is accepted surfaces as a raw SocketException, not wrapped in an HttpRequestException.
            return new ProbeResult(false, ex.SocketErrorCode.ToString());
        }
        catch (IOException ex)
        {
            return new ProbeResult(false, ex.InnerException?.Message ?? ex.Message);
        }
    }
}
