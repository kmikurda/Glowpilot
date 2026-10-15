using Glowpilot.Shared;

namespace Glowpilot.Adapters.CopilotCli;

/// <summary>
/// Maps GitHub Copilot CLI hook event names to the corresponding SessionState and closed flag.
/// </summary>
public static class CopilotHookPayloadMapper
{
    /// <summary>
    /// Maps an event string to a (SessionState, closed) tuple.
    /// Unknown events fall back to Working/false — Copilot CLI may add new events at any time.
    /// </summary>
    /// <param name="eventName">The value of the event field from the Copilot CLI payload.</param>
    /// <returns>The matching SessionState and whether the session should be marked as closed.</returns>
    public static (SessionState State, bool Closed) Map(string eventName) =>
        eventName switch
        {
            "agent:thinking"          => (SessionState.Working, false),
            "agent:tool_call"         => (SessionState.Working, false),
            "agent:tool_result"       => (SessionState.Working, false),
            "agent:waiting_for_input" => (SessionState.Blocked, false),
            "agent:done"              => (SessionState.Idle,    false),
            _                         => (SessionState.Working, false)
        };
}
