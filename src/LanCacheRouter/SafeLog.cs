using System.Text;

namespace LanCacheRouter;

/// <summary>
/// Logging is a side channel: whatever the log destination does, DNS answers, health probing and list refreshes must carry on.
/// These helpers swallow exceptions thrown by a log callback (for example a full disk or a log writer that is shutting down).
/// </summary>
internal static class SafeLog
{
    /// <summary>
    /// Longest untrusted value <see cref="Escape"/> lets through; longer ones are cut so a hostile line cannot flood the log.
    /// </summary>
    public const int MaxEscapedLength = 256;

    /// <summary>
    /// Longest exception message <see cref="Describe"/> lets through.
    /// </summary>
    private const int MaxMessageLength = 1000;

    /// <summary>
    /// Wraps a log callback so that it never throws.
    /// </summary>
    /// <param name="log">The callback to protect.</param>
    /// <returns>A callback that forwards to <paramref name="log"/> and ignores its exceptions.</returns>
    public static Action<string> Wrap(Action<string> log) => message => Write(log, message);

    /// <summary>
    /// Calls a log callback and ignores any exception it throws.
    /// </summary>
    /// <param name="log">The callback.</param>
    /// <param name="message">The message to log.</param>
    public static void Write(Action<string> log, string message)
    {
        try
        {
            log(message);
        }
        catch (Exception)
        {
            // Nothing sensible can be done about a broken logger, and it must not break the caller.
        }
    }

    /// <summary>
    /// Makes a value that someone else controls, such as a queried name or a line of the downloaded domain list, safe to put
    /// into a log line: control characters and line separators (which could forge additional log entries) are written as
    /// <c>\xNN</c> / <c>\uNNNN</c>, and the value is cut after <see cref="MaxEscapedLength"/> characters. Ordinary text is
    /// returned unchanged.
    /// </summary>
    /// <param name="value">The untrusted value.</param>
    /// <returns>The value in a form that is safe to log; an empty string for <c>null</c>.</returns>
    /// <param name="maxLength">Where to cut the value.</param>
    public static string Escape(object? value, int maxLength = MaxEscapedLength)
    {
        string text = value?.ToString() ?? string.Empty;
        bool cut = text.Length > maxLength;
        ReadOnlySpan<char> kept = cut ? text.AsSpan(0, maxLength) : text;

        if (!cut && !NeedsEscaping(kept))
            return text;

        StringBuilder builder = new(kept.Length + 16);
        foreach (char c in kept)
        {
            if (!IsUnsafe(c))
                builder.Append(c);
            else if (c <= 0xFF)
                builder.Append("\\x").Append(((int)c).ToString("X2"));
            else
                builder.Append("\\u").Append(((int)c).ToString("X4"));
        }

        return cut ? builder.Append('…').ToString() : builder.ToString();
    }

    /// <summary>
    /// Describes an exception for the log like <see cref="Exception.ToString"/> does (type, message, stack trace, inner
    /// exceptions), but with the messages escaped by <see cref="Escape"/>. Messages can quote data from the outside, for example
    /// a name from a query or a line of the downloaded list; the stack traces keep their line breaks.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>A multi-line description whose only line breaks are the stack traces'.</returns>
    public static string Describe(Exception exception)
    {
        StringBuilder builder = new();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current != exception)
                builder.Append(" ---> ");

            builder.Append(current.GetType()).Append(": ").Append(Escape(current.Message, MaxMessageLength));

            if (current.StackTrace is { } stackTrace)
                builder.AppendLine().Append(stackTrace);

            if (current.InnerException is not null)
                builder.AppendLine();
        }

        return builder.ToString();
    }

    private static bool NeedsEscaping(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (IsUnsafe(c))
                return true;
        }

        return false;
    }

    private static bool IsUnsafe(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';
}
