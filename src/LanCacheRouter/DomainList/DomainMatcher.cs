using LanCacheRouter.Config;

namespace LanCacheRouter.DomainList;

/// <summary>
/// The active, immutable set of cache domains (ADR 0002). Answers "is this query name a cache domain, and of which
/// service?" without locking. A new matcher is built and swapped in whenever the list or its filters change.
/// </summary>
/// <remarks>
/// Matching rules: names are case-insensitive and a trailing dot is ignored. <c>example.com</c> matches exactly that name.
/// <c>*.example.com</c> matches subdomains at any depth but not <c>example.com</c> itself. If several entries match, an exact
/// entry beats a wildcard, and a deeper wildcard beats a shallower one. A name that matches an excluded pattern never matches.
/// </remarks>
public sealed class DomainMatcher
{
    private readonly DomainTrie _included;
    private readonly DomainTrie _excluded;

    /// <summary>
    /// Creates a matcher from already filled tries.
    /// </summary>
    private DomainMatcher(DomainTrie included, DomainTrie excluded, IReadOnlyDictionary<string, int> serviceCounts)
    {
        _included = included;
        _excluded = excluded;
        ServiceCounts = serviceCounts;
        Count = serviceCounts.Values.Sum();
    }

    /// <summary>
    /// A matcher that matches nothing. Used until the first list is available.
    /// </summary>
    public static DomainMatcher Empty { get; } = new(new DomainTrie(), new DomainTrie(), new Dictionary<string, int>());

    /// <summary>
    /// Number of patterns in the matcher (not the number of matchable names, which is unbounded for wildcards).
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Number of patterns per service, including the services of the configured extra domains.
    /// </summary>
    public IReadOnlyDictionary<string, int> ServiceCounts { get; }

    /// <summary>
    /// Builds a matcher from a downloaded list and the user's filters:
    /// services are filtered by <c>include</c>/<c>exclude</c>, <c>extraDomains</c> are added (always, regardless of the
    /// service filter), and <c>excludeDomains</c> block matching names.
    /// </summary>
    /// <param name="list">The unfiltered list.</param>
    /// <param name="config">The domain-list settings with the filters.</param>
    /// <param name="log">Receives a warning for filter entries that name no service of the list (usually typos).</param>
    /// <returns>The matcher.</returns>
    public static DomainMatcher Create(RawDomainList list, DomainListConfig config, Action<string> log)
    {
        HashSet<string> include = new(config.Services.Include, StringComparer.OrdinalIgnoreCase);
        HashSet<string> exclude = new(config.Services.Exclude, StringComparer.OrdinalIgnoreCase);
        HashSet<string> known = new(list.Services.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        foreach (string name in include.Concat(exclude).Where(n => !known.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            log($"Domain list: service '{name}' in the service filter does not exist in the list");

        DomainTrie included = new();
        DomainTrie excluded = new();
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

        foreach (ServiceDomains service in list.Services)
        {
            if ((include.Count > 0 && !include.Contains(service.Name)) || exclude.Contains(service.Name))
                continue;

            foreach (string pattern in service.Patterns)
                Add(included, counts, pattern, service.Name);
        }

        foreach (ExtraDomainConfig extra in config.ExtraDomains)
            Add(included, counts, extra.Domain, extra.Service);

        foreach (string pattern in config.ExcludeDomains)
        {
            if (DomainName.TryNormalizePattern(pattern, out string normalized, out _))
                excluded.Add(normalized, "excluded");
        }

        return new DomainMatcher(included, excluded, counts);
    }

    /// <summary>
    /// Looks up a query name.
    /// </summary>
    /// <param name="queryName">The name from the DNS question, with or without trailing dot, in any case.</param>
    /// <returns>The name of the service the name belongs to, or <c>null</c> if it is not a cache domain.</returns>
    public string? Match(string queryName)
    {
        string name = queryName.TrimEnd('.').ToLowerInvariant();

        if (name.Length == 0 || _excluded.Match(name) is not null)
            return null;

        return _included.Match(name);
    }

    /// <summary>
    /// Normalizes a pattern and adds it to a trie, counting it for its service. Invalid patterns are skipped.
    /// </summary>
    private static void Add(DomainTrie trie, Dictionary<string, int> counts, string pattern, string service)
    {
        if (!DomainName.TryNormalizePattern(pattern, out string normalized, out _))
            return;

        if (trie.Add(normalized, service))
            counts[service] = counts.GetValueOrDefault(service) + 1;
    }

    /// <summary>
    /// Label trie keyed from the right-most label, so <c>a.b.example.com</c> is stored under com → example → b → a.
    /// </summary>
    private sealed class DomainTrie
    {
        private readonly Node _root = new();

        /// <summary>
        /// Adds a normalized pattern. The first service to claim a pattern keeps it.
        /// </summary>
        /// <param name="pattern">A normalized pattern, <c>example.com</c> or <c>*.example.com</c>.</param>
        /// <param name="service">The service the pattern belongs to.</param>
        /// <returns><c>true</c> if the pattern was new.</returns>
        public bool Add(string pattern, string service)
        {
            bool wildcard = pattern.StartsWith("*.");
            string[] labels = (wildcard ? pattern[2..] : pattern).Split('.');

            Node node = _root;
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                if (!node.Children.TryGetValue(labels[i], out Node? child))
                {
                    child = new Node();
                    node.Children[labels[i]] = child;
                }

                node = child;
            }

            if (wildcard)
            {
                if (node.WildcardService is not null)
                    return false;

                node.WildcardService = service;
                return true;
            }

            if (node.ExactService is not null)
                return false;

            node.ExactService = service;
            return true;
        }

        /// <summary>
        /// Finds the service for a normalized (lower case, no trailing dot) name.
        /// </summary>
        /// <param name="name">The name to look up.</param>
        /// <returns>The service, or <c>null</c> if nothing matches.</returns>
        public string? Match(string name)
        {
            string[] labels = name.Split('.');
            Node node = _root;
            string? wildcard = null;

            for (int i = labels.Length - 1; i >= 0; i--)
            {
                if (!node.Children.TryGetValue(labels[i], out Node? child))
                    return wildcard;

                node = child;

                // A wildcard only covers names below its base, so it counts while labels remain to the left of it.
                if (i > 0 && node.WildcardService is not null)
                    wildcard = node.WildcardService;
            }

            return node.ExactService ?? wildcard;
        }

        /// <summary>
        /// One label level of the trie.
        /// </summary>
        private sealed class Node
        {
            public Dictionary<string, Node> Children { get; } = [];

            public string? ExactService { get; set; }

            public string? WildcardService { get; set; }
        }
    }
}
