using System.Net;
using LanCacheRouter.Config;

namespace LanCacheRouter.Health.Probes;

/// <summary>
/// Chooses the <see cref="IProbe"/> implementation for a <see cref="ProbeType"/>.
/// </summary>
public static class ProbeFactory
{
    private static readonly IProbe Tcp = new TcpProbe();
    private static readonly IProbe Http = new HttpProbe(useTls: false);
    private static readonly IProbe Https = new HttpProbe(useTls: true);
    private static readonly IProbe Always = new AlwaysHealthyProbe();

    /// <summary>
    /// Returns the shared probe for a probe type.
    /// </summary>
    /// <param name="type">The configured probe type.</param>
    /// <returns>The probe implementation.</returns>
    public static IProbe Create(ProbeType type) => type switch
    {
        ProbeType.Tcp => Tcp,
        ProbeType.Http => Http,
        ProbeType.Https => Https,
        ProbeType.None => Always,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown probe type")
    };

    /// <summary>
    /// The <see cref="ProbeType.None"/> probe: never fails, never touches the network.
    /// </summary>
    private sealed class AlwaysHealthyProbe : IProbe
    {
        /// <inheritdoc/>
        public Task<ProbeResult> ProbeAsync(IPAddress address, ResolvedHealthCheck check, CancellationToken cancellationToken)
            => Task.FromResult(new ProbeResult(true, "probing disabled"));
    }
}
