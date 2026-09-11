using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glowpilot.Adapters.ClaudeCode;
using Glowpilot.Shared;

// Default port — will become configurable when issue #8 is implemented.
const int DefaultPort = 57491;

try
{
    var json = await Console.In.ReadToEndAsync();

    var node      = JsonNode.Parse(json) ?? throw new InvalidOperationException("Empty JSON payload.");
    var sessionId = node["session_id"]?.GetValue<string>()      ?? string.Empty;
    var eventName = node["hook_event_name"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Missing required field: hook_event_name.");

    var (state, closed) = HookPayloadMapper.Map(eventName);

    var payload = new SessionEvent(sessionId, Tool.Claude, state, closed);

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    await http.PostAsJsonAsync($"http://localhost:{DefaultPort}/event", payload);
}
catch
{
    // Fail-silent: any exception is swallowed; Claude Code must never be blocked by this adapter.
}

Environment.Exit(0);
