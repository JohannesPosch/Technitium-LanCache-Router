using System.Net;
using Cronos;
using LanCacheRouter.DomainList;
using LanCacheRouter.Routing;

namespace LanCacheRouter.Config;

/// <summary>Semantic validation of a parsed <see cref="RouterConfig"/> (ADR 0003, 0004, 0006). Collects all problems.</summary>
public static class ConfigValidator
{
    /// <summary>
    /// Upper limit for durations in seconds (one day). Timers cannot wait longer than about 49 days, and nothing here is
    /// useful with a longer interval or timeout, so a larger number is almost certainly a typo.
    /// </summary>
    private const int MaxSeconds = 86400;

    /// <summary>
    /// Validates the whole configuration and reports every problem found, not just the first.
    /// </summary>
    /// <param name="config">The configuration to check.</param>
    /// <param name="errors">Problems that make the configuration unusable.</param>
    /// <param name="warnings">Suspicious but accepted settings.</param>
    public static void Validate(RouterConfig config, out List<string> errors, out List<string> warnings)
    {
        errors = [];
        warnings = [];

        // JSON `null` bypasses the non-nullable property defaults, so check the shape before anything dereferences it.
        ValidateStructure(config, errors);
        if (errors.Count > 0)
            return;

        ValidateDomainList(config.DomainList, errors);
        ValidateHealthCheckSettings(config.HealthCheckDefaults, "healthCheckDefaults", errors);
        ValidateCacheServers(config, errors);
        ValidateRoutes(config, errors, warnings);
        ValidateDns(config.Dns, errors);
    }

    /// <summary>
    /// Rejects JSON <c>null</c> where an object, list, list element or text is required. Without this, hand-edited
    /// configs such as <c>"routes": null</c> would crash the later checks instead of being reported.
    /// </summary>
    /// <param name="config">The configuration to check.</param>
    /// <param name="errors">Receives one entry per null.</param>
    private static void ValidateStructure(RouterConfig config, List<string> errors)
    {
        void Require(object? value, string path)
        {
            if (value is null)
                errors.Add($"{path} must not be null");
        }

        Require(config.DomainList, "domainList");
        Require(config.CacheServers, "cacheServers");
        Require(config.Routes, "routes");
        Require(config.Dns, "dns");
        Require(config.Logging, "logging");

        if (config.DomainList is { } list)
        {
            Require(list.BaseUrl, "domainList.baseUrl");
            Require(list.RefreshCron, "domainList.refreshCron");
            Require(list.Services, "domainList.services");
            Require(list.ExtraDomains, "domainList.extraDomains");
            Require(list.ExcludeDomains, "domainList.excludeDomains");

            if (list.Services is { } services)
            {
                Require(services.Include, "domainList.services.include");
                Require(services.Exclude, "domainList.services.exclude");

                if (services.Include?.Contains(null!) == true || services.Exclude?.Contains(null!) == true)
                    errors.Add("domainList.services must not contain null entries");
            }

            if (list.ExtraDomains?.Contains(null!) == true)
                errors.Add("domainList.extraDomains must not contain null entries");

            if (list.ExcludeDomains?.Contains(null!) == true)
                errors.Add("domainList.excludeDomains must not contain null entries");
        }

        if (config.CacheServers?.Contains(null!) == true)
            errors.Add("cacheServers must not contain null entries");

        if (config.Routes is { } routes)
        {
            if (routes.Contains(null!))
                errors.Add("routes must not contain null entries");

            foreach (RouteConfig route in routes.Where(r => r is not null))
                Require(route.Servers, $"routes[priority={route.Priority}].servers");
        }
    }

    /// <summary>
    /// Checks URL, cron expression, timeout and domain patterns of the domain-list settings.
    /// </summary>
    /// <param name="list">The settings to check.</param>
    /// <param name="errors">Receives one entry per problem.</param>
    private static void ValidateDomainList(DomainListConfig list, List<string> errors)
    {
        if (!Uri.TryCreate(list.BaseUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            errors.Add("domainList.baseUrl must be an absolute http(s) URL");

        string[] cronFields = list.RefreshCron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (cronFields.Length is not (5 or 6))
        {
            errors.Add("domainList.refreshCron must have 5 or 6 fields");
        }
        else
        {
            try
            {
                CronExpression.Parse(list.RefreshCron, cronFields.Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
            }
            catch (CronFormatException ex)
            {
                errors.Add($"domainList.refreshCron is invalid: {ex.Message}");
            }
        }

        if (list.HttpTimeoutSeconds is < 1 or > MaxSeconds)
            errors.Add($"domainList.httpTimeoutSeconds must be between 1 and {MaxSeconds}");

        foreach (ExtraDomainConfig extra in list.ExtraDomains)
        {
            if (!DomainName.TryNormalizePattern(extra.Domain, out _, out _))
                errors.Add($"domainList.extraDomains: '{extra.Domain}' is not a valid domain (use example.com or *.example.com)");

            if (string.IsNullOrWhiteSpace(extra.Service))
                errors.Add($"domainList.extraDomains: '{extra.Domain}' needs a service name");
        }

        foreach (string excluded in list.ExcludeDomains)
        {
            if (!DomainName.TryNormalizePattern(excluded, out _, out _))
                errors.Add($"domainList.excludeDomains: '{excluded}' is not a valid domain");
        }

        foreach (string service in list.Services.Include.Intersect(list.Services.Exclude, StringComparer.OrdinalIgnoreCase))
            errors.Add($"domainList.services: '{service}' is both included and excluded");
    }

    /// <summary>
    /// Range-checks the values that are set in a (possibly partial) health-check block.
    /// </summary>
    /// <param name="settings">The block to check; <c>null</c> is accepted and ignored.</param>
    /// <param name="path">Name of the block in the config, used in error messages.</param>
    /// <param name="errors">Receives one entry per problem.</param>
    private static void ValidateHealthCheckSettings(HealthCheckSettings? settings, string path, List<string> errors)
    {
        if (settings is null)
            return;

        if (settings.Port is < 1 or > 65535)
            errors.Add($"{path}.port must be 1-65535");

        if (settings.Path is not null && !settings.Path.StartsWith('/'))
            errors.Add($"{path}.path must start with '/'");

        if (settings.ExpectedStatus is not null && (settings.ExpectedStatus.Count == 0 || settings.ExpectedStatus.Any(s => s is < 100 or > 599)))
            errors.Add($"{path}.expectedStatus must be a non-empty list of HTTP status codes (100-599)");

        if (settings.IntervalSeconds is < 1 or > MaxSeconds)
            errors.Add($"{path}.intervalSeconds must be between 1 and {MaxSeconds}");

        if (settings.TimeoutSeconds is < 1 or > MaxSeconds)
            errors.Add($"{path}.timeoutSeconds must be between 1 and {MaxSeconds}");

        if (settings.FailThreshold is < 1)
            errors.Add($"{path}.failThreshold must be >= 1");

        if (settings.SuccessThreshold is < 1)
            errors.Add($"{path}.successThreshold must be >= 1");

        if (settings.StartupProbe?.FastIntervalSeconds is < 1 or > MaxSeconds)
            errors.Add($"{path}.startupProbe.fastIntervalSeconds must be between 1 and {MaxSeconds}");
    }

    /// <summary>
    /// Checks names, IPv4 address and the effective health check of every cache server.
    /// </summary>
    /// <param name="config">The configuration whose servers are checked.</param>
    /// <param name="errors">Receives one entry per problem.</param>
    private static void ValidateCacheServers(RouterConfig config, List<string> errors)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (CacheServerConfig server in config.CacheServers)
        {
            string path = $"cacheServers['{server.Name}']";

            if (string.IsNullOrWhiteSpace(server.Name))
                errors.Add("cacheServers: every server needs a name");
            else if (!names.Add(server.Name))
                errors.Add($"{path}: duplicate server name");

            if (!Cidr.TryParseStrictAddress(server.Address, out IPAddress? ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                errors.Add($"{path}.address '{server.Address}' is not a valid IPv4 address (IPv6 is not supported in v1)");
            else if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] >= 224)
                errors.Add($"{path}.address '{server.Address}' is not a usable address");

            ValidateHealthCheckSettings(server.HealthCheck, $"{path}.healthCheck", errors);

            ResolvedHealthCheck effective = config.ResolveHealthCheck(server);
            if (effective.TimeoutSeconds > effective.IntervalSeconds)
                errors.Add($"{path}: effective timeoutSeconds ({effective.TimeoutSeconds}) must not exceed intervalSeconds ({effective.IntervalSeconds})");
        }
    }

    /// <summary>
    /// Checks priorities, CIDRs, server lists of the routing table, and warns about
    /// rows that an earlier enabled row makes unreachable.
    /// </summary>
    /// <param name="config">The configuration whose routes are checked.</param>
    /// <param name="errors">Receives one entry per problem.</param>
    /// <param name="warnings">Receives one entry per suspicious setting.</param>
    private static void ValidateRoutes(RouterConfig config, List<string> errors, List<string> warnings)
    {
        HashSet<string> serverNames = new(config.CacheServers.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        HashSet<int> priorities = [];
        List<(RouteConfig Route, Cidr Cidr)> parsed = [];

        foreach (RouteConfig route in config.Routes)
        {
            string path = $"routes[priority={route.Priority}]";

            if (!priorities.Add(route.Priority))
                errors.Add($"{path}: priority is used more than once");

            if (!Cidr.TryParse(route.SourceCidr, out Cidr? cidr))
                errors.Add($"{path}.sourceCidr '{route.SourceCidr}' is not a valid CIDR");
            else if (route.Enabled)
                parsed.Add((route, cidr));

            if (route.Behavior == RouteBehavior.CacheIfHealthy)
            {
                if (route.Servers.Count == 0)
                    errors.Add($"{path}: behavior cacheIfHealthy requires at least one server");

                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                foreach (string? server in route.Servers)
                {
                    if (string.IsNullOrWhiteSpace(server))
                        errors.Add($"{path}.servers: names must not be empty");
                    else if (!serverNames.Contains(server))
                        errors.Add($"{path}.servers: server '{server}' does not exist");
                    else if (!seen.Add(server))
                        errors.Add($"{path}.servers: server '{server}' is listed more than once");
                }
            }
            else if (route.Servers.Count > 0)
            {
                warnings.Add($"{path}: servers are ignored because behavior is passthrough");
            }
        }

        // A row is shadowed if an earlier (lower priority number) enabled row already covers its whole network.
        List<(RouteConfig Route, Cidr Cidr)> ordered = [.. parsed.OrderBy(p => p.Route.Priority)];
        for (int i = 1; i < ordered.Count; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (ordered[j].Cidr.Contains(ordered[i].Cidr))
                {
                    warnings.Add($"routes[priority={ordered[i].Route.Priority}] ({ordered[i].Cidr}) is never used: shadowed by priority {ordered[j].Route.Priority} ({ordered[j].Cidr})");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Range-checks the DNS answer settings.
    /// </summary>
    /// <param name="dns">The settings to check.</param>
    /// <param name="errors">Receives one entry per problem.</param>
    private static void ValidateDns(DnsConfig dns, List<string> errors)
    {
        if (dns.Ttl is < 1 or > 86400)
            errors.Add("dns.ttl must be between 1 and 86400");
    }
}
