namespace LanCacheRouter.Config;

/// <summary>
/// How a cache server's health is probed.
/// </summary>
public enum ProbeType
{
    /// <summary>A TCP connect to the address and port succeeds.</summary>
    Tcp,

    /// <summary>An HTTP GET to the configured path returns an expected status code.</summary>
    Http,

    /// <summary>An HTTPS GET to the configured path returns an expected status code.</summary>
    Https,

    /// <summary>No probing; the address is always considered healthy. Intended for tests and static setups.</summary>
    None
}

/// <summary>
/// What a route does with queries from its source network.
/// </summary>
public enum RouteBehavior
{
    /// <summary>The query is not touched and resolved by Technitium as usual.</summary>
    Passthrough,

    /// <summary>Cache domains are answered with the address of the route's cache server while it is Healthy.</summary>
    CacheIfHealthy
}

/// <summary>
/// What a route does when its primary cache server is not usable.
/// </summary>
public enum FailoverAction
{
    /// <summary>The query is resolved by Technitium as usual.</summary>
    Passthrough,

    /// <summary>The query is answered by a second, named cache server (one hop only).</summary>
    Failover
}

/// <summary>
/// How AAAA and HTTPS/SVCB queries for cache domains are handled.
/// </summary>
public enum NodataMode
{
    /// <summary>Answer with an empty NOERROR response so clients cannot bypass the cache over IPv6 or service hints.</summary>
    Nodata,

    /// <summary>Resolve normally.</summary>
    Passthrough
}

/// <summary>
/// Root of <c>dnsApp.config</c> (ADR 0006). Properties keep their default when omitted from the JSON.
/// </summary>
public sealed class RouterConfig
{
    /// <summary>
    /// Master switch. When <c>false</c> the app is inactive and every query is passed through.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Where the cache-domain list comes from and how it is refreshed and filtered.
    /// </summary>
    public DomainListConfig DomainList { get; init; } = new();

    /// <summary>
    /// Health-check values shared by all cache servers. Partial overrides on top of <see cref="ResolvedHealthCheck.BuiltIn"/>.
    /// </summary>
    public HealthCheckSettings? HealthCheckDefaults { get; init; }

    /// <summary>
    /// The known cache servers. Routes refer to them by name.
    /// </summary>
    public List<CacheServerConfig> CacheServers { get; init; } = [];

    /// <summary>
    /// The routing table. Clients that match no enabled route are passed through.
    /// </summary>
    public List<RouteConfig> Routes { get; init; } = [];

    /// <summary>
    /// Settings for the synthesized DNS answers.
    /// </summary>
    public DnsConfig Dns { get; init; } = new();

    /// <summary>
    /// Log output settings.
    /// </summary>
    public LoggingConfig Logging { get; init; } = new();

    /// <summary>
    /// Computes the effective health check of a server by layering the server's own settings over
    /// <see cref="HealthCheckDefaults"/> over <see cref="ResolvedHealthCheck.BuiltIn"/>.
    /// </summary>
    /// <param name="server">The cache server to resolve the health check for.</param>
    /// <returns>A fully populated health check with no unset values.</returns>
    public ResolvedHealthCheck ResolveHealthCheck(CacheServerConfig server)
    {
        return ResolvedHealthCheck.BuiltIn
            .Apply(HealthCheckDefaults)
            .Apply(server.HealthCheck);
    }
}

/// <summary>
/// Configuration of the cache-domain list (ADR 0002).
/// </summary>
public sealed class DomainListConfig
{
    /// <summary>
    /// Base URL (ending in a slash) that <c>cache_domains.json</c> and the referenced domain files are fetched from.
    /// Defaults to the uklans/cache-domains repository on GitHub.
    /// </summary>
    public string BaseUrl { get; init; } = "https://raw.githubusercontent.com/uklans/cache-domains/master/";

    /// <summary>
    /// Cron expression (5 fields, or 6 with leading seconds) that schedules list refreshes. It is evaluated in the
    /// time zone of the application (the Technitium host or container, for example via the <c>TZ</c> variable).
    /// </summary>
    public string RefreshCron { get; init; } = "0 4 * * *";

    /// <summary>
    /// Whether the list is also refreshed once when the app starts, in addition to the cron schedule.
    /// </summary>
    public bool RefreshOnStartup { get; init; } = true;

    /// <summary>
    /// Timeout in seconds for each HTTP request made while refreshing the list.
    /// </summary>
    public int HttpTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Selects which services of the list are used.
    /// </summary>
    public ServiceFilterConfig Services { get; init; } = new();

    /// <summary>
    /// Domains added by hand on top of the downloaded list.
    /// </summary>
    public List<ExtraDomainConfig> ExtraDomains { get; init; } = [];

    /// <summary>
    /// Domain patterns removed from the list, even if a service contains them.
    /// </summary>
    public List<string> ExcludeDomains { get; init; } = [];
}

/// <summary>
/// Include/exclude filter on service names of the cache-domains list (for example <c>steam</c>).
/// </summary>
public sealed class ServiceFilterConfig
{
    /// <summary>
    /// Services to use. Empty means all services.
    /// </summary>
    public List<string> Include { get; init; } = [];

    /// <summary>
    /// Services to leave out. A service must not appear in both lists.
    /// </summary>
    public List<string> Exclude { get; init; } = [];
}

/// <summary>
/// A domain added to the list by hand.
/// </summary>
public sealed class ExtraDomainConfig
{
    /// <summary>
    /// Domain pattern: <c>example.com</c> matches exactly that name, <c>*.example.com</c> matches its subdomains but not the apex.
    /// </summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>
    /// Service name the domain is attributed to.
    /// </summary>
    public string Service { get; init; } = "custom";
}

/// <summary>
/// Settings for the fast probing that runs while an address has not yet reached a settled state.
/// </summary>
public sealed class StartupProbeSettings
{
    /// <summary>
    /// Whether to probe immediately on start and at the fast interval until the address leaves the Unknown state.
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// Probe interval in seconds used while the startup probe is active.
    /// </summary>
    public int? FastIntervalSeconds { get; init; }
}

/// <summary>
/// Health-check settings where every value is optional, so a cache server can override single values of the defaults.
/// </summary>
public sealed class HealthCheckSettings
{
    /// <summary>
    /// Probe mode.
    /// </summary>
    public ProbeType? Type { get; init; }

    /// <summary>
    /// TCP port that the server's address is probed on.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>
    /// Request path for HTTP(S) probes. Must start with <c>/</c>.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// HTTP status codes that count as a successful HTTP(S) probe.
    /// </summary>
    public List<int>? ExpectedStatus { get; init; }

    /// <summary>
    /// Seconds between two probes of the same address.
    /// </summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>
    /// Seconds after which a single probe counts as failed.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Consecutive failed probes needed to mark an address Unhealthy.
    /// </summary>
    public int? FailThreshold { get; init; }

    /// <summary>
    /// Consecutive successful probes needed to mark an address Healthy.
    /// </summary>
    public int? SuccessThreshold { get; init; }

    /// <summary>
    /// Whether every probe result is logged, not only state changes.
    /// </summary>
    public bool? Verbose { get; init; }

    /// <summary>
    /// Fast-probing behavior while an address is still Unknown.
    /// </summary>
    public StartupProbeSettings? StartupProbe { get; init; }
}

/// <summary>
/// The effective health check of a cache server with no unset values.
/// </summary>
/// <param name="Type">Probe mode.</param>
/// <param name="Port">TCP port that is probed.</param>
/// <param name="Path">Request path for HTTP(S) probes.</param>
/// <param name="ExpectedStatus">HTTP status codes that count as success.</param>
/// <param name="IntervalSeconds">Seconds between two probes of the same address.</param>
/// <param name="TimeoutSeconds">Seconds after which a probe counts as failed.</param>
/// <param name="FailThreshold">Consecutive failures needed to mark an address Unhealthy.</param>
/// <param name="SuccessThreshold">Consecutive successes needed to mark an address Healthy.</param>
/// <param name="Verbose">Whether every probe result is logged.</param>
/// <param name="StartupProbeEnabled">Whether fast probing runs while an address is Unknown.</param>
/// <param name="StartupFastIntervalSeconds">Probe interval in seconds while the startup probe is active.</param>
public sealed record ResolvedHealthCheck(
    ProbeType Type,
    int Port,
    string Path,
    IReadOnlyList<int> ExpectedStatus,
    int IntervalSeconds,
    int TimeoutSeconds,
    int FailThreshold,
    int SuccessThreshold,
    bool Verbose,
    bool StartupProbeEnabled,
    int StartupFastIntervalSeconds)
{
    /// <summary>
    /// The defaults used when neither the server nor <see cref="RouterConfig.HealthCheckDefaults"/> sets a value.
    /// Mirrors the old lancache-checker script: 10 s interval, 2 s timeout, 2 failures / 3 successes.
    /// </summary>
    public static readonly ResolvedHealthCheck BuiltIn = new(
        ProbeType.Http, 80, "/lancache-heartbeat", [200, 204], 10, 2, 2, 3, false, true, 1);

    /// <summary>
    /// Compares by value, including the contents of <see cref="ExpectedStatus"/>. The monitor uses this to decide
    /// whether a server's probing changed and its health state must be reset.
    /// </summary>
    /// <param name="other">The health check to compare with.</param>
    public bool Equals(ResolvedHealthCheck? other)
    {
        return other is not null
            && Type == other.Type
            && Port == other.Port
            && Path == other.Path
            && IntervalSeconds == other.IntervalSeconds
            && TimeoutSeconds == other.TimeoutSeconds
            && FailThreshold == other.FailThreshold
            && SuccessThreshold == other.SuccessThreshold
            && Verbose == other.Verbose
            && StartupProbeEnabled == other.StartupProbeEnabled
            && StartupFastIntervalSeconds == other.StartupFastIntervalSeconds
            && ExpectedStatus.SequenceEqual(other.ExpectedStatus);
    }

    /// <summary>
    /// Hash code consistent with <see cref="Equals(ResolvedHealthCheck?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Type);
        hash.Add(Port);
        hash.Add(Path);
        hash.Add(IntervalSeconds);
        hash.Add(TimeoutSeconds);
        hash.Add(FailThreshold);
        hash.Add(SuccessThreshold);
        hash.Add(Verbose);
        hash.Add(StartupProbeEnabled);
        hash.Add(StartupFastIntervalSeconds);

        foreach (int status in ExpectedStatus)
            hash.Add(status);

        return hash.ToHashCode();
    }

    /// <summary>
    /// Returns a copy of this health check with every value that is set in <paramref name="overrides"/> replaced.
    /// </summary>
    /// <param name="overrides">Partial settings to layer on top, or <c>null</c> for none.</param>
    /// <returns>The merged health check; this instance if there is nothing to override.</returns>
    public ResolvedHealthCheck Apply(HealthCheckSettings? overrides)
    {
        if (overrides is null)
            return this;

        return this with
        {
            Type = overrides.Type ?? Type,
            Port = overrides.Port ?? Port,
            Path = overrides.Path ?? Path,
            ExpectedStatus = overrides.ExpectedStatus ?? ExpectedStatus,
            IntervalSeconds = overrides.IntervalSeconds ?? IntervalSeconds,
            TimeoutSeconds = overrides.TimeoutSeconds ?? TimeoutSeconds,
            FailThreshold = overrides.FailThreshold ?? FailThreshold,
            SuccessThreshold = overrides.SuccessThreshold ?? SuccessThreshold,
            Verbose = overrides.Verbose ?? Verbose,
            StartupProbeEnabled = overrides.StartupProbe?.Enabled ?? StartupProbeEnabled,
            StartupFastIntervalSeconds = overrides.StartupProbe?.FastIntervalSeconds ?? StartupFastIntervalSeconds
        };
    }
}

/// <summary>
/// A cache server (for example a lancache monolithic instance) with a single IP address (ADR 0003).
/// </summary>
public sealed class CacheServerConfig
{
    /// <summary>
    /// Unique name (case-insensitive) that routes refer to.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Administrative switch. A disabled server is not probed and never returned in answers (state Disabled).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// IP address of the server. It is probed, and returned in answers while the server is Healthy. IPv4 only in v1 (backlog B1).
    /// </summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// Health-check values of this server, layered over the defaults. <c>null</c> uses the defaults unchanged.
    /// </summary>
    public HealthCheckSettings? HealthCheck { get; init; }
}

/// <summary>
/// What a route does when its primary server is not usable (ADR 0004).
/// </summary>
public sealed class FailoverConfig
{
    /// <summary>
    /// Whether to pass the query through or use another server.
    /// </summary>
    public FailoverAction Action { get; init; } = FailoverAction.Passthrough;

    /// <summary>
    /// Name of the server to fail over to. Required for <see cref="FailoverAction.Failover"/> and must differ from the route's primary server.
    /// </summary>
    public string? Server { get; init; }
}

/// <summary>
/// One row of the routing table: maps a client network to a cache server (ADR 0004).
/// </summary>
public sealed class RouteConfig
{
    /// <summary>
    /// Evaluation order; the lowest number is checked first and the first match wins. Must be unique.
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Client network this row applies to, in CIDR notation (IPv4 or IPv6). A bare address is a host route.
    /// </summary>
    public string SourceCidr { get; init; } = string.Empty;

    /// <summary>
    /// Name of the primary cache server. Required for <see cref="RouteBehavior.CacheIfHealthy"/>.
    /// </summary>
    public string? Server { get; init; }

    /// <summary>
    /// What to do with matching clients.
    /// </summary>
    public RouteBehavior Behavior { get; init; } = RouteBehavior.CacheIfHealthy;

    /// <summary>
    /// What to do when <see cref="Server"/> is not usable.
    /// </summary>
    public FailoverConfig Failover { get; init; } = new();

    /// <summary>
    /// Free-text note for humans; not used by the app.
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Allows switching a row off without deleting it.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Settings for the synthesized DNS answers (ADR 0005).
/// </summary>
public sealed class DnsConfig
{
    /// <summary>
    /// TTL in seconds of synthesized answers. Kept short so health changes reach clients quickly.
    /// </summary>
    public int Ttl { get; init; } = 60;

    /// <summary>
    /// How AAAA queries for cache domains are handled.
    /// </summary>
    public NodataMode AaaaMode { get; init; } = NodataMode.Nodata;

    /// <summary>
    /// How HTTPS/SVCB queries for cache domains are handled.
    /// </summary>
    public NodataMode HttpsMode { get; init; } = NodataMode.Nodata;
}

/// <summary>
/// Log output settings.
/// </summary>
public sealed class LoggingConfig
{
    /// <summary>
    /// Whether every query that is answered by the app is logged. Intended for debugging only.
    /// State changes, list refreshes and errors are always logged.
    /// </summary>
    public bool Queries { get; init; }
}
