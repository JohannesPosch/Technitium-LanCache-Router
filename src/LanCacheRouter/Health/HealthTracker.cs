namespace LanCacheRouter.Health;

/// <summary>
/// State machine of one cache server (ADR 0003). It counts consecutive probe results and applies the
/// thresholds, replacing the counters of the old lancache-checker script. It does no I/O and no timing.
/// </summary>
/// <remarks>
/// <see cref="RecordResult"/> must be called from one thread at a time (the probe loop of the address).
/// <see cref="State"/> may be read from any thread.
/// </remarks>
public sealed class HealthTracker
{
    private readonly int _failThreshold;
    private readonly int _successThreshold;
    private int _state;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;

    /// <summary>
    /// Creates a tracker, in the <see cref="HealthState.Unknown"/> state unless told otherwise.
    /// </summary>
    /// <param name="failThreshold">Consecutive failed probes needed to become Unhealthy. At least 1.</param>
    /// <param name="successThreshold">Consecutive successful probes needed to become Healthy. At least 1.</param>
    /// <param name="initialState">The state before the first result. Anything but <see cref="HealthState.Disabled"/>, which is not a probe state.</param>
    public HealthTracker(int failThreshold, int successThreshold, HealthState initialState = HealthState.Unknown)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(successThreshold, 1);

        if (initialState == HealthState.Disabled)
            throw new ArgumentOutOfRangeException(nameof(initialState), initialState, "A tracker is never Disabled");

        _failThreshold = failThreshold;
        _successThreshold = successThreshold;
        _state = (int)initialState;
    }

    /// <summary>
    /// The current state. Safe to read from any thread.
    /// </summary>
    public HealthState State => (HealthState)Volatile.Read(ref _state);

    /// <summary>
    /// Feeds one probe result into the state machine.
    /// <list type="bullet">
    /// <item>Unknown becomes Healthy after the success threshold, or Unhealthy after the fail threshold.</item>
    /// <item>Healthy becomes Unhealthy after the fail threshold; a single failure does not flip it.</item>
    /// <item>Unhealthy becomes Healthy after the success threshold.</item>
    /// </list>
    /// A tracker is never Disabled: a disabled server simply has no tracker, and the monitor reports it as Disabled.
    /// </summary>
    /// <param name="success"><c>true</c> if the probe succeeded.</param>
    /// <returns>The state change, or <c>null</c> if the state stayed the same.</returns>
    public HealthTransition? RecordResult(bool success)
    {
        HealthState current = State;

        if (success)
        {
            _consecutiveSuccesses++;
            _consecutiveFailures = 0;
        }
        else
        {
            _consecutiveFailures++;
            _consecutiveSuccesses = 0;
        }

        bool reachedHealthy = _consecutiveSuccesses >= _successThreshold;
        bool reachedUnhealthy = _consecutiveFailures >= _failThreshold;

        HealthState next = current switch
        {
            HealthState.Unknown when reachedHealthy => HealthState.Healthy,
            HealthState.Unknown when reachedUnhealthy => HealthState.Unhealthy,
            HealthState.Healthy when reachedUnhealthy => HealthState.Unhealthy,
            HealthState.Unhealthy when reachedHealthy => HealthState.Healthy,
            _ => current
        };

        if (next == current)
            return null;

        Volatile.Write(ref _state, (int)next);
        return new HealthTransition(current, next);
    }
}
