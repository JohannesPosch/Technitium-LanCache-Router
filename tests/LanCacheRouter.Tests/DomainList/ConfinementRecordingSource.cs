using System.Collections.Concurrent;
using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests.DomainList;

/// <summary>
/// Wraps another <see cref="IDomainSource"/> and records the folder each request was confined to, which is what
/// <c>security.restrictDownloadRedirects</c> decides.
/// </summary>
public sealed class ConfinementRecordingSource(IDomainSource inner) : IDomainSource
{
    /// <summary>The <c>confinedTo</c> argument of every request, in order of arrival.</summary>
    public ConcurrentQueue<Uri?> Confinements { get; } = new();

    public Task<string> GetStringAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
        => GetStringAsync(url, null, timeout, cancellationToken);

    public Task<string> GetStringAsync(Uri url, Uri? confinedTo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Confinements.Enqueue(confinedTo);
        return inner.GetStringAsync(url, timeout, cancellationToken);
    }
}
