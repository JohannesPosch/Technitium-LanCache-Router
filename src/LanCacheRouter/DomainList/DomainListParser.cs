using System.Text.Json;

namespace LanCacheRouter.DomainList;

/// <summary>
/// One entry of <c>cache_domains.json</c>.
/// </summary>
/// <param name="Name">The service name, for example <c>steam</c>.</param>
/// <param name="DomainFiles">Names of the files that list the service's domains.</param>
public sealed record ServiceDefinition(string Name, IReadOnlyList<string> DomainFiles);

/// <summary>
/// Parses the two file formats of the uklans/cache-domains repository (ADR 0002).
/// </summary>
public static class DomainListParser
{
    /// <summary>
    /// Parses <c>cache_domains.json</c>: <c>{ "cache_domains": [ { "name": "...", "domain_files": ["x.txt"] } ] }</c>.
    /// Unknown properties such as <c>description</c> and <c>notes</c> are ignored; entries without a name or files are skipped.
    /// </summary>
    /// <param name="json">The file content.</param>
    /// <returns>The services in file order; services that appear twice are merged.</returns>
    /// <exception cref="FormatException">The content is not the expected JSON structure.</exception>
    public static IReadOnlyList<ServiceDefinition> ParseServiceIndex(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("cache_domains", out JsonElement services)
                || services.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("cache_domains.json must be an object with a 'cache_domains' array");
            }

            Dictionary<string, List<string>> merged = new(StringComparer.OrdinalIgnoreCase);
            List<string> order = [];

            foreach (JsonElement service in services.EnumerateArray())
            {
                if (service.ValueKind != JsonValueKind.Object
                    || !service.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String
                    || !service.TryGetProperty("domain_files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                string name = nameElement.GetString()!.Trim();
                if (name.Length == 0)
                    continue;

                if (!merged.TryGetValue(name, out List<string>? list))
                {
                    list = [];
                    merged[name] = list;
                    order.Add(name);
                }

                list.AddRange(files.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!.Trim()).Where(f => f.Length > 0));
            }

            return [.. order.Select(name => new ServiceDefinition(name, merged[name]))];
        }
        catch (JsonException ex)
        {
            throw new FormatException($"cache_domains.json is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parses a domain file: one entry per line, <c>#</c> starts a comment, blank lines are ignored, trailing whitespace and
    /// CRLF line ends are tolerated. Entries are normalized with <see cref="DomainName.TryNormalizePattern"/>.
    /// </summary>
    /// <param name="content">The file content.</param>
    /// <param name="invalidLines">Receives the lines that were not valid domain patterns and were skipped.</param>
    /// <returns>The valid, normalized patterns in file order, without duplicates.</returns>
    public static IReadOnlyList<string> ParseDomainFile(string content, out IReadOnlyList<string> invalidLines)
    {
        List<string> patterns = [];
        List<string> invalid = [];
        HashSet<string> seen = [];

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine;

            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];

            line = line.Trim();
            if (line.Length == 0)
                continue;

            if (!DomainName.TryNormalizePattern(line, out string normalized, out _))
                invalid.Add(line);
            else if (seen.Add(normalized))
                patterns.Add(normalized);
        }

        invalidLines = invalid;
        return patterns;
    }
}
