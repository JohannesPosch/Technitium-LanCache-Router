using System.Collections.Concurrent;
using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests.DomainList;

/// <summary>
/// In-memory <see cref="IDomainSource"/> serving a fake repository, with call counting and switchable failures.
/// </summary>
public sealed class FakeDomainSource : IDomainSource
{
    public const string BaseUrl = "https://example.test/cache-domains/master/";

    private readonly ConcurrentDictionary<string, string> _files = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();

    /// <summary>When set, every request fails with an <see cref="HttpRequestException"/>.</summary>
    public volatile bool FailAll;

    /// <summary>Requests whose URL ends with this value fail; null for none.</summary>
    public volatile string? FailSuffix;

    public int TotalCalls => _calls.Values.Sum();

    public int IndexCalls => _calls.GetValueOrDefault(BaseUrl + "cache_domains.json");

    public int Calls(string file) => _calls.GetValueOrDefault(BaseUrl + file);

    public FakeDomainSource Set(string file, string content) => SetAt(BaseUrl, file, content);

    /// <summary>Adds a file to another fake repository, for tests that change the source.</summary>
    public FakeDomainSource SetAt(string baseUrl, string file, string content)
    {
        _files[baseUrl + file] = content;
        return this;
    }

    /// <summary>A repository with two services, like the real one in miniature.</summary>
    public static FakeDomainSource WithSteamAndEpic()
    {
        return new FakeDomainSource()
            .Set("cache_domains.json", """
                { "cache_domains": [
                    { "name": "steam",     "description": "Steam",      "domain_files": ["steam.txt"] },
                    { "name": "epicgames", "description": "Epic Games", "domain_files": ["epicgames.txt"] }
                ] }
                """)
            .Set("steam.txt", "# steam\n*.steamcontent.com\nlancache.steamcontent.com\n")
            .Set("epicgames.txt", "epicgames-download1.akamaized.net\n*.download.epicgames.com\n");
    }

    public Task<string> GetStringAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string key = url.ToString();
        _calls.AddOrUpdate(key, 1, (_, count) => count + 1);

        if (FailAll || (FailSuffix is not null && key.EndsWith(FailSuffix)))
            throw new HttpRequestException($"fake failure for {key}");

        if (!_files.TryGetValue(key, out string? content))
            throw new HttpRequestException($"fake 404 for {key}");

        return Task.FromResult(content);
    }
}
