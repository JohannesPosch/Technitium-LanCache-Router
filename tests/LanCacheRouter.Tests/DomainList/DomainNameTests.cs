using LanCacheRouter.DomainList;

namespace LanCacheRouter.Tests.DomainList;

public class DomainNameTests
{
    [Theory]
    [InlineData("example.com", "example.com", false)]
    [InlineData("  Example.COM.  ", "example.com", false)]
    [InlineData("*.cdn.blizzard.com", "*.cdn.blizzard.com", true)]
    [InlineData("lancache.steamcontent.com", "lancache.steamcontent.com", false)]
    [InlineData("_dmarc.example.com", "_dmarc.example.com", false)]
    [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example", false)]
    public void Accepts(string input, string expected, bool wildcard)
    {
        Assert.True(DomainName.TryNormalizePattern(input, out string normalized, out bool isWildcard));
        Assert.Equal(expected, normalized);
        Assert.Equal(wildcard, isWildcard);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("*.com")] // TLD-only wildcard
    [InlineData("com")]
    [InlineData("*.")]
    [InlineData("a.*.example.com")]
    [InlineData("exa mple.com")]
    [InlineData("-bad.example.com")]
    [InlineData("example..com")]
    [InlineData("http://example.com")]
    public void Rejects(string input)
    {
        Assert.False(DomainName.TryNormalizePattern(input, out _, out _));
    }

    [Fact]
    public void Rejects_LabelOver63Chars()
    {
        Assert.False(DomainName.TryNormalizePattern(new string('a', 64) + ".com", out _, out _));
        Assert.True(DomainName.TryNormalizePattern(new string('a', 63) + ".com", out _, out _));
    }

    [Fact]
    public void TotalLength_IsLimitedTo253Characters()
    {
        string label63 = new('a', 63);
        string at253 = $"{label63}.{label63}.{label63}.{new string('b', 61)}"; // 63 + 1 + 63 + 1 + 63 + 1 + 61 = 253
        string at254 = at253 + "c";

        Assert.Equal(253, at253.Length);
        Assert.True(DomainName.TryNormalizePattern(at253, out _, out _));
        Assert.False(DomainName.TryNormalizePattern(at254, out _, out _));
        Assert.True(DomainName.TryNormalizePattern("*." + at253, out _, out bool wildcard));
        Assert.True(wildcard);
    }

    [Theory]
    [InlineData("bücher.example")]
    [InlineData("例え.jp")]
    [InlineData("exаmple.com")] // a Cyrillic 'а' in the middle
    [InlineData("a b.example")]
    [InlineData("a/b.example")]
    [InlineData("a:80.example")]
    [InlineData("user@example.com")]
    [InlineData("a.example.com/path")]
    [InlineData("a.b-.com")]
    [InlineData("a.-b.com")]
    [InlineData("a\tb.example")]
    [InlineData("a\0b.example")]
    [InlineData("**.example.com")]
    [InlineData("*example.com")]
    [InlineData("*.*.example.com")]
    public void Rejects_NonAsciiAndOddlyShapedEntries(string input)
    {
        Assert.False(DomainName.TryNormalizePattern(input, out string normalized, out _));
        Assert.Equal("", normalized);
    }

    [Theory]
    [InlineData("a-b.example.com")]
    [InlineData("a_b.example.com")]
    [InlineData("_sip._tcp.example.com")]
    [InlineData("1.2.3.4.example.com")]
    [InlineData("a.b")]
    [InlineData("A.B")]
    [InlineData("a.example.com.")]
    [InlineData("  a.example.com\t")]
    public void Accepts_OrdinaryHostnames(string input)
    {
        Assert.True(DomainName.TryNormalizePattern(input, out string normalized, out _));
        Assert.Equal(normalized, normalized.ToLowerInvariant());
        Assert.False(normalized.EndsWith('.'));
    }
}
