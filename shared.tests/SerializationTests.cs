using System.Text.Json;
using Glowpilot.Shared;

namespace Glowpilot.Shared.Tests;

public class SessionEventSerializationTests
{
    [Fact]
    public void Deserialize_ValidPayload_MapsAllFields()
    {
        const string json = """{"session_id":"abc","tool":"claude","state":"blocked","closed":false}""";

        var evt = JsonSerializer.Deserialize<SessionEvent>(json);

        Assert.NotNull(evt);
        Assert.Equal("abc", evt.SessionId);
        Assert.Equal("claude", evt.Tool);
        Assert.Equal(SessionState.Blocked, evt.State);
        Assert.False(evt.Closed);
    }

    [Theory]
    [InlineData("blocked", SessionState.Blocked)]
    [InlineData("working", SessionState.Working)]
    [InlineData("idle",    SessionState.Idle)]
    public void Deserialize_State_VariousValues(string stateStr, SessionState expected)
    {
        var json = $$"""{"session_id":"x","tool":"claude","state":"{{stateStr}}","closed":false}""";

        var evt = JsonSerializer.Deserialize<SessionEvent>(json);

        Assert.Equal(expected, evt!.State);
    }

    [Fact]
    public void Deserialize_ClosedDefault_IsFalse()
    {
        const string json = """{"session_id":"x","tool":"claude","state":"idle"}""";

        var evt = JsonSerializer.Deserialize<SessionEvent>(json);

        Assert.False(evt!.Closed);
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesSnakeCaseKeys()
    {
        var evt = new SessionEvent("abc", "claude", SessionState.Blocked, false);

        var json = JsonSerializer.Serialize(evt);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("session_id", out _), "missing session_id");
        Assert.True(doc.RootElement.TryGetProperty("tool", out _),       "missing tool");
        Assert.True(doc.RootElement.TryGetProperty("state", out _),      "missing state");
        Assert.True(doc.RootElement.TryGetProperty("closed", out _),     "missing closed");
    }
}

public class WireFrameSerializationTests
{
    [Theory]
    [InlineData(RingState.Blocked,    "blocked")]
    [InlineData(RingState.Working,    "working")]
    [InlineData(RingState.Idle,       "idle")]
    [InlineData(RingState.NoSessions, "no-sessions")]
    public void Serialize_State_AsLowercaseString(RingState state, string expected)
    {
        var frame = new WireFrame(state, "claude");

        var json = JsonSerializer.Serialize(frame);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void Serialize_ToExpectedJson()
    {
        var frame = new WireFrame(RingState.Blocked, "claude");

        var json = JsonSerializer.Serialize(frame);

        Assert.Equal("""{"state":"blocked","tool":"claude"}""", json);
    }

    [Fact]
    public void Serialize_SnakeCaseKeys()
    {
        var frame = new WireFrame(RingState.Idle, Tool.Copilot);

        var json = JsonSerializer.Serialize(frame);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("state", out _), "missing state");
        Assert.True(doc.RootElement.TryGetProperty("tool", out _),  "missing tool");
    }
}

public class ToolConstantsTests
{
    [Fact]
    public void Claude_Constant_IsCorrectString() => Assert.Equal("claude", Tool.Claude);

    [Fact]
    public void Copilot_Constant_IsCorrectString() => Assert.Equal("copilot", Tool.Copilot);
}
