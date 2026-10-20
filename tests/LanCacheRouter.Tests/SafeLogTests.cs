namespace LanCacheRouter.Tests;

public class SafeLogTests
{
    [Fact]
    public void Wrap_ForwardsMessages()
    {
        List<string> lines = [];

        SafeLog.Wrap(lines.Add)("hello");

        Assert.Equal(["hello"], lines);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(NullReferenceException))]
    public void Wrap_SwallowsWhateverTheLoggerThrows(Type exceptionType)
    {
        Action<string> broken = _ => throw (Exception)Activator.CreateInstance(exceptionType, nonPublic: true)!;

        SafeLog.Wrap(broken)("hello"); // must not throw
        SafeLog.Write(broken, "hello");
    }

    [Theory]
    [InlineData("cdn1.steamcontent.com")]
    [InlineData("ümlaut.example")]
    [InlineData("")]
    public void Escape_LeavesOrdinaryTextAlone(string text)
    {
        Assert.Equal(text, SafeLog.Escape(text));
    }

    [Theory]
    [InlineData("a\nb", "a\\x0Ab")]
    [InlineData("a\r\nLanCache Router: forged", "a\\x0D\\x0ALanCache Router: forged")]
    [InlineData("a\tb", "a\\x09b")]
    [InlineData("a\0b", "a\\x00b")]
    [InlineData("a\u001bb", "a\\x1Bb")]
    [InlineData("a\u0085b", "a\\x85b")] // next line
    [InlineData("a\u2028b", "a\\u2028b")] // line separator
    [InlineData("a\u2029b", "a\\u2029b")] // paragraph separator
    public void Escape_WritesControlCharactersAndLineBreaksAsText(string text, string expected)
    {
        Assert.Equal(expected, SafeLog.Escape(text));
    }

    [Fact]
    public void Escape_CutsVeryLongValues()
    {
        string escaped = SafeLog.Escape(new string('a', 100_000));

        Assert.Equal(SafeLog.MaxEscapedLength + 1, escaped.Length);
        Assert.EndsWith("…", escaped);
    }

    [Fact]
    public void Escape_AcceptsNullAndNonStrings()
    {
        Assert.Equal("", SafeLog.Escape(null));
        Assert.Equal("https://example.test/", SafeLog.Escape(new Uri("https://example.test/")));
    }

    [Fact]
    public void Describe_EscapesMessagesOfTheWholeChain_ButKeepsTheStackTrace()
    {
        Exception thrown;
        try
        {
            try
            {
                throw new InvalidOperationException("inner\nforged line");
            }
            catch (Exception inner)
            {
                throw new FormatException("outer\r\nLanCache Router: forged", inner);
            }
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        string description = SafeLog.Describe(thrown);
        string[] lines = description.Split('\n');

        Assert.Contains("System.FormatException: outer\\x0D\\x0ALanCache Router: forged", description);
        Assert.Contains("System.InvalidOperationException: inner\\x0Aforged line", description);
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("forged line") || l.StartsWith("LanCache Router: forged"));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("at ") && l.Contains(nameof(Describe_EscapesMessagesOfTheWholeChain_ButKeepsTheStackTrace)));
    }

    [Fact]
    public void Write_KeepsLoggingAfterAFailure()
    {
        int calls = 0;
        Action<string> flaky = _ =>
        {
            if (++calls == 1)
                throw new IOException("first call fails");
        };
        Action<string> safe = SafeLog.Wrap(flaky);

        safe("one");
        safe("two");

        Assert.Equal(2, calls);
    }
}
