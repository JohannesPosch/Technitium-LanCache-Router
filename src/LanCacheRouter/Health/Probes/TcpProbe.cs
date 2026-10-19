using System.Net;
using System.Net.Sockets;
using LanCacheRouter.Config;

namespace LanCacheRouter.Health.Probes;

/// <summary>
/// Succeeds if a TCP connection to the address and port can be established (the script's <c>PROBE_MODE=tcp</c>).
/// </summary>
public sealed class TcpProbe : IProbe
{
    /// <inheritdoc/>
    public async Task<ProbeResult> ProbeAsync(IPAddress address, ResolvedHealthCheck check, CancellationToken cancellationToken)
    {
        using TcpClient client = new(address.AddressFamily);

        try
        {
            await client.ConnectAsync(address, check.Port, cancellationToken);
            return new ProbeResult(true, "connected");
        }
        catch (SocketException ex)
        {
            return new ProbeResult(false, ex.SocketErrorCode.ToString());
        }
    }
}
