using Glowpilot.Shared;

namespace Glowpilot.Adapters.ClaudeCode;

/// <summary>
/// Maps Claude Code hook event names to the corresponding SessionState and closed flag.
/// </summary>
public static class HookPayloadMapper
{
    /// <summary>
    /// Maps a hook_event_name string to a (SessionState, closed) tuple.
    /// </summary>
    /// <param name="sessionEventName">The value of the hook_event_name field from the Claude Code payload.</param>
    /// <returns>The matching SessionState and whether the session should be marked as closed.</returns>
    /// <exception cref="ArgumentException">Thrown for unknown session event names.</exception>
    public static (SessionState State, bool Closed) Map(string sessionEventName) =>
        sessionEventName switch
        {
            "PreToolUse"   => (SessionState.Working, false),
            "PostToolUse"  => (SessionState.Working, false),
            "Notification" => (SessionState.Blocked,  false),
            "Stop"         => (SessionState.Idle,     true),
            _              => throw new ArgumentException($"Unknown session event: {sessionEventName}", nameof(sessionEventName))
        };
}
