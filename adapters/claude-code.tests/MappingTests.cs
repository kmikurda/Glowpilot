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
    public void Notification_MapsTo_Blocked_NotClosed()
    {
        var (state, closed) = HookPayloadMapper.Map("Notification");

        Assert.Equal(SessionState.Blocked, state);
        Assert.False(closed);
    }

    [Fact]
    public void Stop_MapsTo_Idle_Closed()
    {
        var (state, closed) = HookPayloadMapper.Map("Stop");

        Assert.Equal(SessionState.Idle, state);
        Assert.True(closed);
    }
}
