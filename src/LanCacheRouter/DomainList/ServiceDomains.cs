namespace LanCacheRouter.DomainList;

/// <summary>
/// The domain patterns of one service of the cache-domains list, for example <c>steam</c>.
/// </summary>
/// <param name="Name">The service name from <c>cache_domains.json</c>.</param>
/// <param name="Patterns">Normalized patterns: <c>example.com</c> (exact) or <c>*.example.com</c> (subdomains, not the apex).</param>
public sealed record ServiceDomains(string Name, IReadOnlyList<string> Patterns);

/// <summary>
/// A complete, unfiltered download of the cache-domains list. This is what gets persisted, so changing the
/// include/exclude settings never requires a new download.
/// </summary>
/// <param name="FetchedAt">When the list was downloaded.</param>
/// <param name="BaseUrl">The source the list came from. A persisted list from a different source is ignored.</param>
/// <param name="Services">All services of the list in file order.</param>
public sealed record RawDomainList(DateTimeOffset FetchedAt, string BaseUrl, IReadOnlyList<ServiceDomains> Services);
