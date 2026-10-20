using System.Text.RegularExpressions;
using LanCacheRouter.Config;
using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests;

/// <summary>Marks a theory that reads the repository's documentation; skipped when the tests run without the source tree.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DocumentationTheoryAttribute : TheoryAttribute
{
    public DocumentationTheoryAttribute()
    {
        if (DocumentationTests.RepositoryRoot is null)
            Skip = "the repository's docs folder is not next to the test binaries";
    }
}

/// <summary>
/// The wiki and the ADRs are the specification of this app. Every configuration example in them must be accepted by the real
/// validator, and every link between the pages must lead somewhere, so the documents cannot drift away from the code unnoticed.
/// </summary>
public partial class DocumentationTests
{
    internal static string? RepositoryRoot { get; } = FindRepositoryRoot();

    private static string? FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LanCacheRouter.slnx")) && Directory.Exists(Path.Combine(directory.FullName, "docs")))
                return directory.FullName;
        }

        return null;
    }

    private static IEnumerable<string> MarkdownFiles()
    {
        if (RepositoryRoot is null)
            return [];

        return Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "docs"), "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(RepositoryRoot, "README.md"))
            .Order();
    }

    /// <summary>All fenced <c>json</c>/<c>jsonc</c> blocks, as (file relative to the repository, block number in the file, content).</summary>
    public static TheoryData<string, int, string> JsonBlocks()
    {
        TheoryData<string, int, string> data = [];

        foreach (string file in MarkdownFiles())
        {
            int block = 0;
            bool inJson = false;
            List<string> lines = [];

            foreach (string line in File.ReadLines(file))
            {
                if (!inJson && (line.StartsWith("```json") || line.StartsWith("```jsonc")))
                {
                    inJson = true;
                    lines = [];
                }
                else if (inJson && line.StartsWith("```"))
                {
                    inJson = false;
                    data.Add(Path.GetRelativePath(RepositoryRoot!, file), ++block, string.Join('\n', lines));
                }
                else if (inJson)
                {
                    lines.Add(line);
                }
            }
        }

        if (data.Count == 0)
            data.Add("(none)", 0, "");

        return data;
    }

    private static readonly string[] DomainListKeys = ["baseUrl", "refreshCron", "refreshOnStartup", "httpTimeoutSeconds", "services", "extraDomains", "excludeDomains"];

    /// <summary>
    /// Turns a documentation snippet into a complete configuration, or returns <c>null</c> for snippets that are not meant to be one
    /// (placeholders). Fragments are wrapped in the parent they belong to, and servers named by routes are added.
    /// </summary>
    private static string? ToConfiguration(string snippet)
    {
        string text = snippet.Trim();
        if (text.Length == 0 || text.Contains('…') || text.Contains('<'))
            return null;

        string config;
        if (text.StartsWith('{') && text.Contains("\"priority\"") && !text.Contains("\"routes\"") && !text.Contains("\"cacheServers\""))
            config = $"{{ \"routes\": [ {text} ] }}"; // bare route objects
        else if (text.StartsWith('{'))
            config = text; // a complete document
        else if (text.StartsWith('"'))
            config = DomainListKeys.Contains(Regex.Match(text, "^\"([^\"]+)\"").Groups[1].Value) ? $"{{ \"domainList\": {{ {text} }} }}" : $"{{ {text} }}";
        else
            return null;

        if (config.Contains("\"routes\"") && !config.Contains("\"cacheServers\""))
        {
            string[] names = [.. ServerList().Matches(config).SelectMany(m => QuotedName().Matches(m.Groups[1].Value)).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase)];
            string servers = string.Join(", ", names.Select((name, i) => $"{{ \"name\": \"{name}\", \"address\": \"10.99.0.{i + 1}\" }}"));
            config = config.Insert(config.IndexOf('{') + 1, $" \"cacheServers\": [ {servers} ],");
        }

        return config;
    }

    [GeneratedRegex("\"servers\"\\s*:\\s*\\[([^\\]]*)\\]")]
    private static partial Regex ServerList();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedName();

    [DocumentationTheory]
    [MemberData(nameof(JsonBlocks))]
    public void EveryConfigurationExampleInTheDocs_IsAcceptedWithoutWarnings(string file, int block, string snippet)
    {
        if (snippet.Contains("\"cache_domains\""))
        {
            Assert.NotEmpty(DomainListParser.ParseServiceIndex(snippet)); // the upstream index format, not an app configuration
            return;
        }

        string? config = ToConfiguration(snippet);
        if (config is null)
            return; // a placeholder such as "{ … }"

        ConfigParseResult result = ConfigParser.Parse(config);

        Assert.True(result.IsValid, $"{file}, JSON block {block}: {string.Join("; ", result.Errors)}\n{config}");
        Assert.True(result.Warnings.Count == 0, $"{file}, JSON block {block}: {string.Join("; ", result.Warnings)}");
    }

    [DocumentationTheory]
    [InlineData("README.md")]
    [InlineData("docs/wiki/03-installation.md")]
    [InlineData("docs/wiki/04-configuration-reference.md")]
    public void TheMainPagesContainAtLeastOneCompleteExample(string page)
    {
        int complete = JsonBlocks().Select(row => (object[])row).Count(row => (string)row[0] == page.Replace('/', Path.DirectorySeparatorChar)
            && ToConfiguration((string)row[2]) is { } config && config.Contains("\"cacheServers\"") && ConfigParser.Parse(config).IsValid);

        Assert.True(complete >= 1, $"{page} should show a configuration that works");
    }

    [GeneratedRegex(@"\[[^\]]*\]\((?<target>[^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^#{1,6}\s+(?<title>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    /// <summary>GitHub's rule for heading anchors: lower case, spaces become hyphens, punctuation is dropped.</summary>
    private static string Slug(string heading)
    {
        string plain = Regex.Replace(heading, @"[`*_]", "").ToLowerInvariant();
        return Regex.Replace(plain.Replace(' ', '-'), @"[^a-z0-9\-_]", "");
    }

    public static TheoryData<string> Pages()
    {
        TheoryData<string> data = [];
        foreach (string file in MarkdownFiles())
            data.Add(Path.GetRelativePath(RepositoryRoot!, file));

        if (data.Count == 0)
            data.Add("(none)");

        return data;
    }

    [DocumentationTheory]
    [MemberData(nameof(Pages))]
    public void EveryRelativeLink_LeadsToAnExistingPageAndHeading(string page)
    {
        string path = Path.Combine(RepositoryRoot!, page);
        string text = File.ReadAllText(path);
        List<string> broken = [];

        foreach (Match match in MarkdownLink().Matches(text))
        {
            string target = match.Groups["target"].Value;
            if (target.StartsWith("http://") || target.StartsWith("https://") || target.StartsWith("mailto:"))
                continue;

            string[] parts = target.Split('#', 2);
            string file = parts[0].Length == 0 ? path : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, parts[0]));

            if (!File.Exists(file) && !Directory.Exists(file))
            {
                broken.Add($"{target} (no such file)");
                continue;
            }

            if (parts.Length == 2 && file.EndsWith(".md"))
            {
                string[] anchors = [.. Heading().Matches(File.ReadAllText(file)).Select(m => Slug(m.Groups["title"].Value))];
                if (!anchors.Contains(parts[1]))
                    broken.Add($"{target} (no such heading)");
            }
        }

        Assert.True(broken.Count == 0, $"{page}: {string.Join("; ", broken)}");
    }
}
