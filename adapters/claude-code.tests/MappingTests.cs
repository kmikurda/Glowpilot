using Glowpilot.Adapters.ClaudeCode;
using Glowpilot.Shared;

namespace Glowpilot.Adapters.ClaudeCode.Tests;

public class MappingTests
{
    [Fact]
    public void PreToolUse_MapsTo_Working_NotClosed()
    {
        var (state, closed) = HookPayloadMapper.Map("PreToolUse");

        Assert.Equal(SessionState.Working, state);
        Assert.False(closed);
    }

    [Fact]
    public void PostToolUse_MapsTo_Working_NotClosed()
    {
        var (state, closed) = HookPayloadMapper.Map("PostToolUse");

        Assert.Equal(SessionState.Working, state);
        Assert.False(closed);
    }

    [Fact]
    public void Notification_MapsTo_Idle_NotClosed()
    {
        // Notification fires when Claude finishes and notifies the user — it is a "done" signal,
        // not an active-work signal. Mapping to Idle (green) matches the Stop event semantics
        // and prevents the LED from bouncing back to yellow after Stop already set it green.
        var (state, closed) = HookPayloadMapper.Map("Notification");

        Assert.Equal(SessionState.Idle, state);
        Assert.False(closed);
    }

    [Fact]
    public void Stop_MapsTo_Idle_NotClosed()
    {
        // Stop means Claude finished responding and is waiting for input — the session
        // is idle (green LED), not gone. Stale-timeout eviction cleans it up after
        // 10 minutes of inactivity so the ring eventually returns to NoSessions.
        var (state, closed) = HookPayloadMapper.Map("Stop");

        Assert.Equal(SessionState.Idle, state);
        Assert.False(closed);
    }
}
