namespace LanCacheRouter;

/// <summary>
/// Logging is a side channel: whatever the log destination does, DNS answers, health probing and list refreshes must carry on.
/// These helpers swallow exceptions thrown by a log callback (for example a full disk or a log writer that is shutting down).
/// </summary>
internal static class SafeLog
{
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
}
