using System.Text.RegularExpressions;

namespace LanCacheRouter.DomainList;

/// <summary>Validation and normalisation of domain entries as found in cache-domains lists and in the app config.</summary>
public static partial class DomainName
{
    /// <summary>
    /// A single DNS label: 1 to 63 characters of letters, digits, hyphen or underscore, not starting or ending with a hyphen.
    /// Input is lower-cased before matching.
    /// </summary>
    [GeneratedRegex("^[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$")]
    private static partial Regex LabelRegex();

    /// <summary>
    /// Accepts "example.com" (exact) or "*.example.com" (subdomains only, apex excluded).
    /// The result is lower case without a trailing dot. At least two labels are required (no TLD-only entries).
    /// </summary>
    /// <param name="input">The raw entry, for example a line of a domain file or an extra domain from the config.</param>
    /// <param name="normalized">The normalized pattern; empty if the entry is invalid.</param>
    /// <param name="isWildcard"><c>true</c> if the entry started with <c>*.</c>.</param>
    /// <returns><c>true</c> if the entry is a valid domain pattern.</returns>
    public static bool TryNormalizePattern(string? input, out string normalized, out bool isWildcard)
    {
        normalized = string.Empty;
        isWildcard = false;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        string value = input.Trim().ToLowerInvariant().TrimEnd('.');

        if (value.StartsWith("*."))
        {
            isWildcard = true;
            value = value[2..];
        }

        if (value.Length is 0 or > 253)
            return false;

        string[] labels = value.Split('.');
        if (labels.Length < 2)
            return false;

        foreach (string label in labels)
        {
            if (!LabelRegex().IsMatch(label))
                return false;
        }

        normalized = isWildcard ? "*." + value : value;
        return true;
    }
}
