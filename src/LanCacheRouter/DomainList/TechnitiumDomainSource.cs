using DnsServerCore.ApplicationCommon;
using TechnitiumLibrary.Net.Http.Client;

namespace LanCacheRouter.DomainList;

/// <summary>
/// <see cref="IDomainSource"/> that downloads through Technitium's own HTTP stack, so the server's configured outbound
/// proxy and IPv4/IPv6 preference apply, exactly as for its block-list downloads.
/// </summary>
public sealed class TechnitiumDomainSource : IDomainSource
{
    private readonly Func<IDnsServer> _server;

    /// <summary>
    /// Creates a source.
    /// </summary>
    /// <param name="server">Returns the current DNS server. A function, because the server instance is only known once the app is initialized.</param>
    public TechnitiumDomainSource(Func<IDnsServer> server)
    {
        _server = server;
    }

    /// <inheritdoc/>
    public Task<string> GetStringAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
        => GetStringAsync(url, null, timeout, cancellationToken);

    /// <inheritdoc cref="HttpDomainSource.GetStringAsync(Uri, Uri, TimeSpan, CancellationToken)"/>
    public async Task<string> GetStringAsync(Uri url, Uri? confinedTo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        IDnsServer server = _server();

        using HttpClientNetworkHandler handler = new()
        {
            Proxy = server.Proxy,
            NetworkType = HttpClientNetworkHandler.GetNetworkType(server.IPv6Mode),
            DnsClient = server
        };

        using HttpClient client = new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd("Technitium DNS Server LanCacheRouter");

        return await new HttpDomainSource(client).GetStringAsync(url, confinedTo, timeout, cancellationToken);
    }
}
