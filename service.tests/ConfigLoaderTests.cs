using Glowpilot.Service;

namespace Glowpilot.Service.Tests;

public class ConfigLoaderTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempConfig(string json)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles.Where(File.Exists))
            File.Delete(f);
    }

    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid()}.json");

        var config = ConfigLoader.Load(path);

        Assert.Equal(10, config.StaleTimeoutMinutes);
        Assert.Equal("#0044FF", config.NoSessionsColor);
        Assert.Equal(57491, config.HttpPort);
    }

    // ── Values from file ─────────────────────────────────────────────────────

    [Fact]
    public void Load_ValidFile_ReturnsValuesFromFile()
    {
        var path = CreateTempConfig("""
            {
                "staleTimeoutMinutes": 20,
                "noSessionsColor": "#FF0000",
                "httpPort": 12345
            }
            """);

        var config = ConfigLoader.Load(path);

        Assert.Equal(20, config.StaleTimeoutMinutes);
        Assert.Equal("#FF0000", config.NoSessionsColor);
        Assert.Equal(12345, config.HttpPort);
    }

    // ── Forward compatibility ─────────────────────────────────────────────────

    [Fact]
    public void Load_UnknownKeys_DoesNotThrow()
    {
        var path = CreateTempConfig("""{"staleTimeoutMinutes": 5, "unknownField": "value"}""");

        var ex = Record.Exception(() => ConfigLoader.Load(path));

        Assert.Null(ex);
    }

    // ── Malformed JSON ────────────────────────────────────────────────────────

    [Fact]
    public void Load_BadJson_ReturnsDefaults()
    {
        var path = CreateTempConfig("not-valid-json{{{");

        var config = ConfigLoader.Load(path);

        Assert.Equal(10, config.StaleTimeoutMinutes);
        Assert.Equal("#0044FF", config.NoSessionsColor);
        Assert.Equal(57491, config.HttpPort);
    }
}
