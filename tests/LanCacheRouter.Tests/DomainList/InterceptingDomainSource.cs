using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests.DomainList;

/// <summary>
/// Wraps another <see cref="IDomainSource"/> so a test can hold back, delay or fail requests at precise moments.
/// </summary>
public sealed class InterceptingDomainSource(IDomainSource inner) : IDomainSource
{
    /// <summary>Awaited before the request is forwarded; throw from it to fail the request.</summary>
    public Func<Uri, CancellationToken, Task>? BeforeRequest { get; set; }

    /// <summary>Awaited after the inner source has answered, before the answer is handed to the caller.</summary>
    public Func<Uri, CancellationToken, Task>? AfterResponse { get; set; }

    public async Task<string> GetStringAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (BeforeRequest is { } before)
            await before(url, cancellationToken);

        string content = await inner.GetStringAsync(url, timeout, cancellationToken);

        if (AfterResponse is { } after)
            await after(url, cancellationToken);

        return content;
    }
}
