using System.Net;
using LanCacheRouter.Config;

namespace LanCacheRouter.Health.Probes;

/// <summary>
/// Outcome of a single probe.
/// </summary>
/// <param name="Success"><c>true</c> if the address answered as expected.</param>
/// <param name="Detail">Short human-readable reason, used in verbose logs, for example <c>HTTP 204</c> or <c>connection refused</c>.</param>
public readonly record struct ProbeResult(bool Success, string Detail);

/// <summary>
/// Checks whether one address of a cache server is alive (replaces the probe functions of check-lancache.sh).
/// </summary>
public interface IProbe
{
    /// <summary>
    /// Probes one address once.
    /// </summary>
    /// <param name="address">The address to probe.</param>
    /// <param name="check">The effective health check, which supplies port, path and expected status codes.</param>
    /// <param name="cancellationToken">Cancelled on shutdown or when the probe timeout expires.</param>
    /// <returns>The result. Expected failures such as refused connections are returned as unsuccessful results, not thrown.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<ProbeResult> ProbeAsync(IPAddress address, ResolvedHealthCheck check, CancellationToken cancellationToken);
}
