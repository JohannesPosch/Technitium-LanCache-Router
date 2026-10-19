namespace LanCacheRouter.DomainList;

/// <summary>
/// Downloads the complete cache-domains list: <c>cache_domains.json</c> and every domain file it references (ADR 0002).
/// The download is all-or-nothing; if any file fails, nothing is returned so the previous list stays active.
/// </summary>
public static class DomainListFetcher
{
    /// <summary>
    /// Name of the service index file in the repository.
    /// </summary>
    public const string IndexFileName = "cache_domains.json";

    /// <summary>
    /// Downloads and parses the whole list.
    /// </summary>
    /// <param name="baseUrl">Base URL of the repository's raw files. A trailing slash is added if missing.</param>
    /// <param name="timeout">Timeout for each single request.</param>
    /// <param name="source">Performs the downloads.</param>
    /// <param name="log">Receives warnings, for example about skipped invalid lines.</param>
    /// <param name="now">The time to record as <see cref="RawDomainList.FetchedAt"/>.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The unfiltered list.</returns>
    /// <exception cref="FormatException">The index is malformed, or it references a file outside the base URL.</exception>
    /// <exception cref="HttpRequestException">A download failed.</exception>
    /// <exception cref="TimeoutException">A download timed out.</exception>
    public static async Task<RawDomainList> FetchAsync(
        string baseUrl,
        TimeSpan timeout,
        IDomainSource source,
        Action<string> log,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Uri baseUri = new(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute);

        string indexJson = await source.GetStringAsync(new Uri(baseUri, IndexFileName), timeout, cancellationToken);
        IReadOnlyList<ServiceDefinition> definitions = DomainListParser.ParseServiceIndex(indexJson);

        if (definitions.Count == 0)
            throw new FormatException($"{IndexFileName} lists no services");

        Dictionary<string, IReadOnlyList<string>> fileCache = new(StringComparer.Ordinal);
        List<ServiceDomains> services = [];

        foreach (ServiceDefinition definition in definitions)
        {
            List<string> patterns = [];
            HashSet<string> seen = [];

            foreach (string file in definition.DomainFiles)
            {
                if (!fileCache.TryGetValue(file, out IReadOnlyList<string>? filePatterns))
                {
                    Uri fileUri = ResolveFile(baseUri, file);
                    string content = await source.GetStringAsync(fileUri, timeout, cancellationToken);
                    filePatterns = DomainListParser.ParseDomainFile(content, out IReadOnlyList<string> invalid);

                    if (invalid.Count > 0)
                        log($"Domain list: skipped {invalid.Count} invalid line(s) in {file}, first: '{invalid[0]}'");

                    fileCache[file] = filePatterns;
                }

                patterns.AddRange(filePatterns.Where(seen.Add));
            }

            services.Add(new ServiceDomains(definition.Name, patterns));
        }

        return new RawDomainList(now, baseUri.ToString(), services);
    }

    /// <summary>
    /// Resolves a domain file name against the base URL and makes sure it stays on the same server and below the base path,
    /// so a malicious index cannot make the app request arbitrary URLs.
    /// </summary>
    /// <param name="baseUri">The base URL, ending in a slash.</param>
    /// <param name="file">The file name from the index.</param>
    /// <returns>The absolute URL of the file.</returns>
    /// <exception cref="FormatException">The file name points outside the base URL.</exception>
    private static Uri ResolveFile(Uri baseUri, string file)
    {
        if (!Uri.TryCreate(baseUri, file, out Uri? resolved)
            || resolved.Scheme != baseUri.Scheme
            || !string.Equals(resolved.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase)
            || !resolved.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal)
            || file.Contains(".."))
        {
            throw new FormatException($"Domain file '{file}' points outside the base URL and is rejected");
        }

        return resolved;
    }
}
