using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests.DomainList;

public class DomainListParserTests
{
    [Fact]
    public void ServiceIndex_ParsesNamesAndFiles_IgnoringOtherProperties()
    {
        IReadOnlyList<ServiceDefinition> services = DomainListParser.ParseServiceIndex("""
            { "cache_domains": [
                { "name": "steam", "description": "Valve", "notes": "n", "domain_files": ["steam.txt"] },
                { "name": "nintendo", "domain_files": ["nintendo.txt", "nintendo2.txt"], "mixed_content": true }
            ] }
            """);

        Assert.Equal(["steam", "nintendo"], services.Select(s => s.Name));
        Assert.Equal(["steam.txt"], services[0].DomainFiles);
        Assert.Equal(["nintendo.txt", "nintendo2.txt"], services[1].DomainFiles);
    }

    [Fact]
    public void ServiceIndex_MergesDuplicateNames_AndSkipsIncompleteEntries()
    {
        IReadOnlyList<ServiceDefinition> services = DomainListParser.ParseServiceIndex("""
            { "cache_domains": [
                { "name": "a", "domain_files": ["1.txt"] },
                { "name": "A", "domain_files": ["2.txt"] },
                { "description": "no name", "domain_files": ["x.txt"] },
                { "name": "nofiles" },
                { "name": "  ", "domain_files": ["y.txt"] },
                { "name": "b", "domain_files": [] }
            ] }
            """);

        Assert.Equal(["a", "b"], services.Select(s => s.Name));
        Assert.Equal(["1.txt", "2.txt"], services[0].DomainFiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "cache_domains": {} }""")]
    public void ServiceIndex_RejectsMalformedContent(string json)
    {
        Assert.Throws<FormatException>(() => DomainListParser.ParseServiceIndex(json));
    }

    [Fact]
    public void DomainFile_HandlesCommentsBlankLinesCrlfAndWhitespace()
    {
        IReadOnlyList<string> patterns = DomainListParser.ParseDomainFile(
            "# Steam\r\n\r\n*.steamcontent.com  \r\n\tlancache.steamcontent.com\t\r\n  # indented comment\r\nCDN.Example.COM # trailing comment\r\n",
            out IReadOnlyList<string> invalid);

        Assert.Equal(["*.steamcontent.com", "lancache.steamcontent.com", "cdn.example.com"], patterns);
        Assert.Empty(invalid);
    }

    [Fact]
    public void DomainFile_ReportsInvalidLines_AndDropsDuplicates()
    {
        IReadOnlyList<string> patterns = DomainListParser.ParseDomainFile(
            "a.com\n*.com\nA.COM\nbad domain\n*\nb.com",
            out IReadOnlyList<string> invalid);

        Assert.Equal(["a.com", "b.com"], patterns);
        Assert.Equal(["*.com", "bad domain", "*"], invalid);
    }

    [Fact]
    public void DomainFile_Empty_YieldsNothing()
    {
        Assert.Empty(DomainListParser.ParseDomainFile("", out IReadOnlyList<string> invalid));
        Assert.Empty(invalid);
    }

    [Fact]
    public void DomainFile_CommentMarkerEndsTheEntry_EvenWithoutSpace()
    {
        IReadOnlyList<string> patterns = DomainListParser.ParseDomainFile("a.com#comment\nb.com # comment\n#c.com\n##\n#", out IReadOnlyList<string> invalid);

        Assert.Equal(["a.com", "b.com"], patterns);
        Assert.Empty(invalid);
    }

    [Fact]
    public void DomainFile_HandlesUnicodeWhitespaceAndMixedLineEndings()
    {
        IReadOnlyList<string> patterns = DomainListParser.ParseDomainFile("\u00a0a.com\u00a0\r\nb.com\n\r\n\tc.com \n", out IReadOnlyList<string> invalid);

        Assert.Equal(["a.com", "b.com", "c.com"], patterns);
        Assert.Empty(invalid);
    }

    [Fact]
    public void DomainFile_LargeFile_IsParsedCompletely()
    {
        string content = string.Join("\n", Enumerable.Range(0, 200000).Select(i => $"host{i}.example.com"));

        IReadOnlyList<string> patterns = DomainListParser.ParseDomainFile(content, out IReadOnlyList<string> invalid);

        Assert.Equal(200000, patterns.Count);
        Assert.Empty(invalid);
    }

    [Fact]
    public void DomainFile_LinesWithOnlyJunk_AreReportedNotThrown()
    {
        IReadOnlyList<string> patterns = DomainListParser.ParseDomainFile("\0\0\n\u200b\n{ \"json\": true }\n<html>\n*.ok.example\n", out IReadOnlyList<string> invalid);

        Assert.Equal(["*.ok.example"], patterns);
        Assert.Equal(4, invalid.Count);
    }

    [Theory]
    [InlineData("""{ "cache_domains": [ null ] }""")]
    [InlineData("""{ "cache_domains": [ 5, "text", [] ] }""")]
    [InlineData("""{ "cache_domains": [ { "name": 5, "domain_files": ["a.txt"] } ] }""")]
    [InlineData("""{ "cache_domains": [ { "name": "a", "domain_files": "a.txt" } ] }""")]
    [InlineData("""{ "cache_domains": [ { "name": "a", "domain_files": [1, null, {}] } ] }""")]
    public void ServiceIndex_SkipsEntriesOfTheWrongShape_WithoutThrowing(string json)
    {
        IReadOnlyList<ServiceDefinition> services = DomainListParser.ParseServiceIndex(json);

        Assert.All(services, s => Assert.All(s.DomainFiles, f => Assert.False(string.IsNullOrWhiteSpace(f))));
    }

    [Fact]
    public void ServiceIndex_VeryDeepJson_IsAFormatError_NotAStackOverflow()
    {
        string json = string.Concat(Enumerable.Repeat("[", 100000));

        Assert.Throws<FormatException>(() => DomainListParser.ParseServiceIndex(json));
    }

    [Fact]
    public void ServiceIndex_ABomIsNotValidJson_SoTheSourceMustStripIt()
    {
        // Documents why HttpDomainSource removes a byte order mark: the parser itself does not accept one.
        Assert.Throws<FormatException>(() => DomainListParser.ParseServiceIndex("\uFEFF{ \"cache_domains\": [] }"));
    }
}
