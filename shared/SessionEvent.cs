using System.Text.Json.Serialization;

namespace Glowpilot.Shared;

public record SessionEvent(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("tool")]       string Tool,
    [property: JsonPropertyName("state")]      SessionState State,
    [property: JsonPropertyName("closed")]     bool Closed = false
);
