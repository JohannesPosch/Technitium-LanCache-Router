using System.Net;

namespace LanCacheRouter.Health;

/// <summary>
/// The health of one cache server at a point in time.
/// </summary>
/// <param name="Name">The server name from the configuration.</param>
/// <param name="State">The state of the server. A disabled server is Disabled; otherwise it is the state of its probed address.</param>
/// <param name="Address">The server's IP address. Only return it in DNS answers if <see cref="IsUsable"/> is <c>true</c>.</param>
public sealed record ServerHealth(string Name, HealthState State, IPAddress Address)
{
    /// <summary>
    /// <c>true</c> if the server may be used for answers, that is, it is Healthy.
    /// </summary>
    public bool IsUsable => State == HealthState.Healthy;
}

/// <summary>
/// Immutable view of the health of all cache servers. The request path reads a snapshot without locking;
/// the <see cref="HealthMonitor"/> publishes a new one whenever something changes.
/// </summary>
public sealed class HealthSnapshot
{
    private readonly Dictionary<string, ServerHealth> _servers;

    /// <summary>
    /// A snapshot that knows no servers.
    /// </summary>
    public static readonly HealthSnapshot Empty = new([]);

    /// <summary>
    /// Creates a snapshot from the given server health entries.
    /// </summary>
    /// <param name="servers">The entries; names must be unique ignoring case.</param>
    public HealthSnapshot(IEnumerable<ServerHealth> servers)
    {
        _servers = servers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// All servers in the snapshot.
    /// </summary>
    public IReadOnlyCollection<ServerHealth> Servers => _servers.Values;

    /// <summary>
    /// Looks up a server by name, ignoring case.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <returns>The server health, or <c>null</c> if the server is not known.</returns>
    public ServerHealth? Get(string name) => _servers.GetValueOrDefault(name);
}
