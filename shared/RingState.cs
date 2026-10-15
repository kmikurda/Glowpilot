using System.Text.Json.Serialization;

namespace Glowpilot.Shared;

[JsonConverter(typeof(JsonStringEnumConverter<RingState>))]
public enum RingState
{
    [JsonStringEnumMemberName("blocked")]
    Blocked,

    [JsonStringEnumMemberName("working")]
    Working,

    [JsonStringEnumMemberName("working-stale")]
    WorkingStale,

    [JsonStringEnumMemberName("idle")]
    Idle,

    [JsonStringEnumMemberName("no-sessions")]
    NoSessions
}
