namespace LanCacheRouter.DomainList;

/// <summary>
/// Downloads text files for the domain list. Abstracted so tests can run without network access and so the app can
/// later route requests through Technitium's configured proxy.
/// </summary>
public interface IDomainSource
{
    /// <summary>
    /// Downloads a text file.
    /// </summary>
    /// <param name="url">The absolute URL.</param>
    /// <param name="timeout">Maximum time for the whole request.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The response body as text.</returns>
    /// <exception cref="HttpRequestException">The request failed or returned a non-success status.</exception>
    /// <exception cref="TimeoutException">The request took longer than <paramref name="timeout"/>.</exception>
    Task<string> GetStringAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a text file and, if <paramref name="confinedTo"/> is given, rejects a response that does not come from
    /// inside it. A source that follows redirects must check the final location, because a redirect can lead anywhere.
    /// The default implementation ignores <paramref name="confinedTo"/>, which is right for sources that cannot redirect.
    /// </summary>
    /// <param name="url">The absolute URL.</param>
    /// <param name="confinedTo">The folder the response must come from (same scheme, host, port and below the path), or <c>null</c> for no restriction.</param>
    /// <param name="timeout">Maximum time for the whole request.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The response body as text.</returns>
    /// <exception cref="HttpRequestException">The request failed, returned a non-success status, or was redirected out of <paramref name="confinedTo"/>.</exception>
    /// <exception cref="TimeoutException">The request took longer than <paramref name="timeout"/>.</exception>
    Task<string> GetStringAsync(Uri url, Uri? confinedTo, TimeSpan timeout, CancellationToken cancellationToken)
        => GetStringAsync(url, timeout, cancellationToken);
}
