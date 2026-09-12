using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Glowpilot.Service;

/// <summary>
/// Immutable runtime configuration for the Glowpilot Service.
/// Loaded from the platform config file; defaults are used when the file is absent or malformed.
/// </summary>
public sealed record GlowpilotConfig : ISessionMapConfig
{
    /// <inheritdoc />
    public int StaleTimeoutMinutes { get; init; } = 10;

    /// <inheritdoc />
    public string NoSessionsColor { get; init; } = "#0044FF";

    /// <summary>TCP port on which the Service HTTP server listens.</summary>
    public int HttpPort { get; init; } = 57491;
}

/// <summary>
/// Loads <see cref="GlowpilotConfig"/> from the platform-specific config file,
/// falling back to defaults when the file is absent or malformed.
/// </summary>
public static class ConfigLoader
{
    /// <summary>
    /// Platform default: <c>%APPDATA%\Glowpilot\config.json</c> (Windows)
    /// or <c>~/.config/glowpilot/config.json</c> (Linux/macOS).
    /// </summary>
    private static string DefaultConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Glowpilot",
            "config.json");

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Loads from the platform default config path.</summary>
    public static GlowpilotConfig Load(ILogger? logger = null) =>
        Load(DefaultConfigPath, logger);

    /// <summary>Loads from <paramref name="path"/>; intended for testing.</summary>
    public static GlowpilotConfig Load(string path, ILogger? logger = null)
    {
        if (!File.Exists(path))
            return new GlowpilotConfig();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GlowpilotConfig>(json, _options)
                   ?? new GlowpilotConfig();
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex,
                "Failed to parse Glowpilot config at {Path}. Using defaults.", path);
            return new GlowpilotConfig();
        }
    }
}
