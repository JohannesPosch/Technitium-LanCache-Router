using LanCacheRouter.Config;

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
    /// <param name="restrictRedirects">Whether a response that a redirect moved outside the base URL is rejected; see <see cref="SecurityConfig.RestrictDownloadRedirects"/>.</param>
    /// <param name="limits">Bounds for the size of the download; <see cref="DomainListLimits.Default"/> if <c>null</c>.</param>
    /// <returns>The unfiltered list.</returns>
    /// <exception cref="FormatException">The index is malformed, it references a file outside the base URL, or the download exceeds <paramref name="limits"/>.</exception>
    /// <exception cref="HttpRequestException">A download failed.</exception>
    /// <exception cref="TimeoutException">A download timed out.</exception>
    public static async Task<RawDomainList> FetchAsync(
        string baseUrl,
        TimeSpan timeout,
        IDomainSource source,
        Action<string> log,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool restrictRedirects = true,
        DomainListLimits? limits = null)
    {
        limits ??= DomainListLimits.Default;
        Uri baseUri = new(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute);
        long totalCharacters = 0;
        int totalPatterns = 0;

        async Task<string> Download(Uri url)
        {
            string content = await source.GetStringAsync(url, restrictRedirects ? baseUri : null, timeout, cancellationToken);

            totalCharacters += content.Length;
            if (totalCharacters > limits.MaxTotalCharacters)
                throw new FormatException($"The domain list is larger than the limit of {limits.MaxTotalCharacters} characters in total");

            return content;
        }

        string indexJson = await Download(new Uri(baseUri, IndexFileName));
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
                    if (fileCache.Count >= limits.MaxFiles)
                        throw new FormatException($"The domain list references more than the limit of {limits.MaxFiles} files");

                    Uri fileUri = ResolveFile(baseUri, file);
                    string content = await Download(fileUri);
                    filePatterns = DomainListParser.ParseDomainFile(content, out IReadOnlyList<string> invalid);

                    if (invalid.Count > 0)
                        log($"Domain list: skipped {invalid.Count} invalid line(s) in {SafeLog.Escape(file)}, first: '{SafeLog.Escape(invalid[0])}'");

                    fileCache[file] = filePatterns;
                }

                List<string> added = [.. filePatterns.Where(seen.Add)];

                totalPatterns += added.Count;
                if (totalPatterns > limits.MaxPatterns)
                    throw new FormatException($"The domain list has more than the limit of {limits.MaxPatterns} patterns in total");

                patterns.AddRange(added);
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
            || !UriScope.IsWithin(resolved, baseUri)
            || file.Contains(".."))
        {
            throw new FormatException($"Domain file '{SafeLog.Escape(file)}' points outside the base URL and is rejected");
        }

        return resolved;
    }
}
