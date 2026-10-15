using System.Collections.Concurrent;
using Glowpilot.Shared;

namespace Glowpilot.Service;

public interface ISessionMapConfig
{
    int StaleTimeoutMinutes { get; }

    /// <summary>
    /// How long an Idle session is kept before eviction.
    /// Shorter than StaleTimeoutMinutes so a closed session transitions to
    /// NoSessions within a few minutes rather than waiting the full 10-minute timeout.
    /// </summary>
    int IdleStaleTimeoutSeconds { get; }

    /// <summary>
    /// How long a Working session can go without a new event before transitioning to
    /// WorkingStale. Signals that the agent is likely waiting for user input.
    /// </summary>
    int WorkingStaleTimeoutSeconds { get; }

    /// <summary>
    /// How long a newly-entered Blocked state is suppressed before the LED shows red.
    /// Filters silent/auto-handled permission checks that resolve within milliseconds
    /// without ever showing a dialog. A real interactive prompt persists past this window.
    /// </summary>
    int BlockedDebounceSeconds { get; }

    /// <summary>Color displayed by the Device when Ring State is No-Sessions.</summary>
    string NoSessionsColor { get; }
}

public sealed record SessionMapConfig(
    int StaleTimeoutMinutes,
    int IdleStaleTimeoutSeconds = 60,
    int WorkingStaleTimeoutSeconds = 30,
    int BlockedDebounceSeconds = 3,
    string NoSessionsColor = "#0044FF") : ISessionMapConfig;

internal sealed record SessionEntry(string Tool, SessionState State, DateTimeOffset LastSeen, DateTimeOffset StateSince);

/// <summary>
/// Thread-safe in-memory record of all tracked sessions.
/// Key: session ID. Value: tool, current state, last-seen timestamp.
/// </summary>
public sealed class SessionMap
{
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly TimeProvider _time;

    public SessionMap(TimeProvider time)
    {
        _time = time;
    }

    public void Upsert(SessionEvent evt)
    {
        var now = _time.GetUtcNow();
        _sessions.AddOrUpdate(
            evt.SessionId,
            _ => new SessionEntry(evt.Tool, evt.State, now, now),
            (_, existing) => new SessionEntry(
                evt.Tool,
                evt.State,
                now,
                existing.State == evt.State ? existing.StateSince : now));
    }

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>
    /// Removes sessions whose last-seen timestamp is older than <paramref name="defaultTimeout"/>,
    /// or older than <paramref name="idleTimeout"/> when their state is Idle.
    /// Idle sessions are evicted sooner so a finished session transitions to blue within
    /// a few minutes rather than waiting the full 10-minute working-session timeout.
    /// Returns the sessions that were removed, so the caller can report them.
    /// </summary>
    public IReadOnlyList<SessionStatusEntry> EvictStale(TimeSpan defaultTimeout, TimeSpan idleTimeout)
    {
        var now = _time.GetUtcNow();
        var evicted = new List<SessionStatusEntry>();
        foreach (var (key, entry) in _sessions.ToArray())
        {
            var timeout = entry.State == SessionState.Idle ? idleTimeout : defaultTimeout;
            if (entry.LastSeen < now - timeout && _sessions.TryRemove(key, out _))
                evicted.Add(ToStatusEntry(key, entry));
        }
        return evicted;
    }

    /// <summary>Removes sessions whose last-seen timestamp is older than <paramref name="staleTimeout"/> (all states).</summary>
    public IReadOnlyList<SessionStatusEntry> EvictStale(TimeSpan staleTimeout) => EvictStale(staleTimeout, staleTimeout);

    /// <summary>
    /// Computes the highest-priority Ring State from all active sessions.
    /// Priority: Blocked > Working (or WorkingStale) > Idle > NoSessions.
    /// <para>
    /// <paramref name="workingStaleTimeout"/>: a Working session with no event for longer than this
    /// is reported as <see cref="RingState.WorkingStale"/> — the agent is likely paused.
    /// </para>
    /// <para>
    /// <paramref name="blockedDebounce"/>: a newly-entered Blocked state is suppressed for this
    /// window and treated as Working. Filters silent/auto-handled permission checks that never
    /// show a dialog; a real interactive prompt persists past the debounce period.
    /// </para>
    /// </summary>
    public WireFrame ComputeRingState(TimeSpan? workingStaleTimeout = null, TimeSpan? blockedDebounce = null)
    {
        var sessions = _sessions.Values.ToList();

        if (sessions.Count == 0)
            return new WireFrame(RingState.NoSessions, string.Empty);

        var now = _time.GetUtcNow();

        var blocked = sessions.FirstOrDefault(s => s.State == SessionState.Blocked);
        if (blocked is not null)
        {
            bool debouncing = blockedDebounce.HasValue && now - blocked.StateSince < blockedDebounce.Value;
            if (!debouncing)
                return new WireFrame(RingState.Blocked, blocked.Tool);
            // Within debounce window — fall through and treat as Working.
        }

        // A debounced Blocked session stands in for Working so the LED doesn't flash red briefly.
        var working = sessions.FirstOrDefault(s => s.State == SessionState.Working) ?? blocked;
        if (working is not null)
        {
            if (workingStaleTimeout.HasValue && now - working.LastSeen > workingStaleTimeout.Value)
                return new WireFrame(RingState.WorkingStale, working.Tool);
            return new WireFrame(RingState.Working, working.Tool);
        }

        var idle = sessions.FirstOrDefault(s => s.State == SessionState.Idle);
        if (idle is not null)
            return new WireFrame(RingState.Idle, idle.Tool);

        return new WireFrame(RingState.NoSessions, string.Empty);
    }

    /// <summary>Returns a snapshot of all sessions (for the /status endpoint).</summary>
    public IReadOnlyList<SessionStatusEntry> GetAll() =>
        _sessions
            .Select(kvp => ToStatusEntry(kvp.Key, kvp.Value))
            .ToList()
            .AsReadOnly();

    private static SessionStatusEntry ToStatusEntry(string sessionId, SessionEntry entry) =>
        new()
        {
            SessionId = sessionId,
            Tool      = entry.Tool,
            State     = entry.State,
            LastSeen  = entry.LastSeen
        };
}
