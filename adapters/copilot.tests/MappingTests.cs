using Glowpilot.Adapters.CopilotCli;
using Glowpilot.Shared;

namespace Glowpilot.Adapters.CopilotCli.Tests;

public class MappingTests
{
    [Fact]
    public void AgentThinking_MapsTo_Working_NotClosed()
    {
        var (state, closed) = CopilotHookPayloadMapper.Map("agent:thinking");

        Assert.Equal(SessionState.Working, state);
        Assert.False(closed);
    }

    [Fact]
    public void AgentToolCall_MapsTo_Working_NotClosed()
    {
        var (state, closed) = CopilotHookPayloadMapper.Map("agent:tool_call");

        Assert.Equal(SessionState.Working, state);
        Assert.False(closed);
    }

    [Fact]
    public void AgentToolResult_MapsTo_Working_NotClosed()
    {
        var (state, closed) = CopilotHookPayloadMapper.Map("agent:tool_result");

        Assert.Equal(SessionState.Working, state);
        Assert.False(closed);
    }

    [Fact]
    public void AgentWaitingForInput_MapsTo_Blocked_NotClosed()
    {
        var (state, closed) = CopilotHookPayloadMapper.Map("agent:waiting_for_input");

        Assert.Equal(SessionState.Blocked, state);
        Assert.False(closed);
    }

    [Fact]
    public void AgentDone_MapsTo_Idle_NotClosed()
    {
        // Done means the agent finished — session is idle (green LED), not removed.
        // Stale-timeout eviction cleans it up after IdleStaleTimeoutSeconds of inactivity.
        var (state, closed) = CopilotHookPayloadMapper.Map("agent:done");

        Assert.Equal(SessionState.Idle, state);
        Assert.False(closed);
    }

    [Fact]
    public void UnknownEvent_MapsTo_Working_NotClosed()
    {
        var (state, closed) = CopilotHookPayloadMapper.Map("agent:some_future_event");

        Assert.Equal(SessionState.Working, state);
        Assert.False(closed);
    }

    [Fact]
    public void EmptyString_MapsTo_Working_NotClosed()
    {
        var (state, closed) = CopilotHookPayloadMapper.Map(string.Empty);

        Assert.Equal(SessionState.Working, state);
        Assert.False(closed);
    }
}
