using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanCacheRouter.Config;

/// <summary>
/// Outcome of parsing and validating <c>dnsApp.config</c>.
/// </summary>
/// <param name="Config">The parsed configuration, or <c>null</c> if there were errors.</param>
/// <param name="Errors">Problems that make the configuration unusable. Empty when valid.</param>
/// <param name="Warnings">Suspicious but accepted settings, for example a route that can never match.</param>
public sealed record ConfigParseResult(RouterConfig? Config, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// <c>true</c> if <see cref="Config"/> is usable, that is, there are no errors.
    /// </summary>
    public bool IsValid => Config is not null && Errors.Count == 0;
}

/// <summary>Parses and validates dnsApp.config. Never throws for bad input; problems are reported as errors.</summary>
public static class ConfigParser
{
    /// <summary>
    /// Strict serializer settings: camelCase names and enum values, unknown properties and numeric enums rejected;
    /// comments and trailing commas tolerated because the config is edited by hand in a text area.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>
    /// Deserializes and validates a configuration document.
    /// </summary>
    /// <param name="json">The content of <c>dnsApp.config</c>; <c>null</c> when the file does not exist.</param>
    /// <returns>A result with the configuration if it is valid, otherwise the list of errors. Never throws for bad input.</returns>
    public static ConfigParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Failure("configuration is empty");

        RouterConfig? config;

        try
        {
            config = JsonSerializer.Deserialize<RouterConfig>(json, Options);
        }
        catch (JsonException ex)
        {
            return Failure($"invalid JSON: {ex.Message}");
        }

        if (config is null)
            return Failure("configuration must be a JSON object");

        ConfigValidator.Validate(config, out List<string> errors, out List<string> warnings);
        return new ConfigParseResult(errors.Count == 0 ? config : null, errors, warnings);
    }

    /// <summary>
    /// Builds a result for input that could not be turned into a configuration at all.
    /// </summary>
    /// <param name="error">The reason.</param>
    private static ConfigParseResult Failure(string error) => new(null, [error], []);
}
