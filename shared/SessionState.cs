using System.Text.Json.Serialization;

namespace Glowpilot.Shared;

[JsonConverter(typeof(JsonStringEnumConverter<SessionState>))]
public enum SessionState
{
    [JsonStringEnumMemberName("blocked")]
    Blocked,

    [JsonStringEnumMemberName("working")]
    Working,

    [JsonStringEnumMemberName("idle")]
    Idle
}
