namespace LanCacheRouter.DomainList;

/// <summary>
/// <see cref="IDomainSource"/> backed by an <see cref="HttpClient"/>, with a size limit so a misbehaving server
/// cannot exhaust memory.
/// </summary>
public sealed class HttpDomainSource : IDomainSource
{
    /// <summary>
    /// Default size limit per file. The largest real domain file is a few kilobytes.
    /// </summary>
    public const long DefaultMaxBytes = 5 * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly long _maxBytes;

    /// <summary>
    /// Creates a source.
    /// </summary>
    /// <param name="client">The client to use; its own <see cref="HttpClient.Timeout"/> should be infinite because the timeout is passed per call.</param>
    /// <param name="maxBytes">Largest accepted response body.</param>
    public HttpDomainSource(HttpClient client, long maxBytes = DefaultMaxBytes)
    {
        _client = client;
        _maxBytes = maxBytes;
    }

    /// <inheritdoc/>
    public Task<string> GetStringAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
        => GetStringAsync(url, null, timeout, cancellationToken);

    /// <remarks>
    /// The client follows redirects, and the final location is only known once the response arrives, so the check rejects the
    /// response (its body is never read or used) but cannot stop the request to the redirect target from being sent.
    /// </remarks>
    /// <inheritdoc/>
    public async Task<string> GetStringAsync(Uri url, Uri? confinedTo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using HttpResponseMessage response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);

            if (confinedTo is not null)
            {
                Uri? finalUrl = response.RequestMessage?.RequestUri;
                if (finalUrl is null || !UriScope.IsWithin(finalUrl, confinedTo))
                    throw new HttpRequestException($"Request to {url} was redirected to {SafeLog.Escape(finalUrl)}, which is outside {confinedTo}, and is rejected");
            }

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > _maxBytes)
                throw new HttpRequestException($"Response from {url} is larger than {_maxBytes} bytes");

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[16 * 1024];

            int read;
            while ((read = await stream.ReadAsync(chunk, timeoutSource.Token)) > 0)
            {
                if (buffer.Length + read > _maxBytes)
                    throw new HttpRequestException($"Response from {url} is larger than {_maxBytes} bytes");

                buffer.Write(chunk, 0, read);
            }

            // Files saved on Windows often start with a byte order mark. It is not text, and it would break the JSON index
            // and invalidate the first domain of every list.
            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Request to {url} timed out after {timeout.TotalSeconds:0.#}s");
        }
    }
}
