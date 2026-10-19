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
