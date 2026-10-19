namespace LanCacheRouter.Health;

/// <summary>
/// Health state of a cache server address or of a whole cache server (ADR 0003).
/// </summary>
public enum HealthState
{
    /// <summary>Starting up or waiting for the first conclusive probe results. Never returned in DNS answers.</summary>
    Unknown,

    /// <summary>Passed enough consecutive probes. Eligible for DNS answers.</summary>
    Healthy,

    /// <summary>Failed enough consecutive probes. Must not be returned in DNS answers.</summary>
    Unhealthy,

    /// <summary>Administratively excluded. Not probed and never returned in DNS answers.</summary>
    Disabled
}

/// <summary>
/// A change from one <see cref="HealthState"/> to another.
/// </summary>
/// <param name="From">The previous state.</param>
/// <param name="To">The new state.</param>
public readonly record struct HealthTransition(HealthState From, HealthState To)
{
    /// <summary>
    /// Formats the transition for logs, for example <c>Unknown -> Healthy</c>.
    /// </summary>
    public override string ToString() => $"{From} -> {To}";
}
