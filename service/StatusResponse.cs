using System.Text.Json.Serialization;
using Glowpilot.Shared;

namespace Glowpilot.Service;

/// <summary>One session's snapshot, returned by <c>GET /status</c>.</summary>
public sealed record SessionStatusEntry
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("tool")]
    public string Tool { get; init; } = "";

    [JsonPropertyName("state")]
    public SessionState State { get; init; }

    [JsonPropertyName("lastSeen")]
    public DateTimeOffset LastSeen { get; init; }
}

/// <summary>Full response body for <c>GET /status</c>.</summary>
public sealed record StatusResponse
{
    [JsonPropertyName("sessions")]
    public IReadOnlyList<SessionStatusEntry> Sessions { get; init; } = [];

    [JsonPropertyName("ringState")]
    public RingState RingState { get; init; }

    [JsonPropertyName("deviceConnected")]
    public bool DeviceConnected { get; init; }
}
