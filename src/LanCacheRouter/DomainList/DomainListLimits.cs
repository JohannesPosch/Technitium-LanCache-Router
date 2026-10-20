namespace LanCacheRouter.DomainList;

/// <summary>
/// Upper bounds for one download of the domain list, so a hostile or broken source cannot use up the server's memory.
/// The real list is a few kilobytes with about 130 entries, so the defaults leave a wide margin. A download that exceeds
/// a limit fails as a whole, like any other failed download. The limits are configurable in the <c>security</c> section.
/// </summary>
/// <param name="MaxFiles">Most distinct domain files (not counting the index) that one download may fetch.</param>
/// <param name="MaxTotalCharacters">Most text, summed over the index and all domain files, that one download may read.</param>
/// <param name="MaxPatterns">Most patterns, summed over all services, that one download may produce.</param>
public sealed record DomainListLimits(int MaxFiles, long MaxTotalCharacters, int MaxPatterns)
{
    /// <summary>
    /// The limits used unless a caller passes others: 500 files, 20 million characters, 100,000 patterns.
    /// </summary>
    public static DomainListLimits Default { get; } = new(500, 20_000_000, 100_000);
}
