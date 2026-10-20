namespace LanCacheRouter.DomainList;

/// <summary>
/// The rule that keeps downloads of the domain list on the server and below the path of the configured base URL.
/// </summary>
internal static class UriScope
{
    /// <summary>
    /// Tells whether a URL has the same scheme and authority (host and port) as <paramref name="root"/> and lies below its path.
    /// </summary>
    /// <param name="candidate">The absolute URL to test.</param>
    /// <param name="root">The absolute root URL; its path should end in a slash.</param>
    /// <returns><c>true</c> if <paramref name="candidate"/> is inside <paramref name="root"/>.</returns>
    public static bool IsWithin(Uri candidate, Uri root)
        => candidate.Scheme == root.Scheme
            && string.Equals(candidate.Authority, root.Authority, StringComparison.OrdinalIgnoreCase)
            && candidate.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal);
}
