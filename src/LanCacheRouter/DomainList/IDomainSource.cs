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
}
