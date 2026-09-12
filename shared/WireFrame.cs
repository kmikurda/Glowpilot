using System.Text.Json.Serialization;

namespace Glowpilot.Shared;

public record WireFrame(
    [property: JsonPropertyName("state")] RingState State,
    [property: JsonPropertyName("tool")]  string Tool,
    [property: JsonPropertyName("color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null
);
