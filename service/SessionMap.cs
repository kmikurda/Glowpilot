using System.Collections.Concurrent;
using Glowpilot.Shared;

namespace Glowpilot.Service;

public interface ISessionMapConfig
{
    int StaleTimeoutMinutes { get; }

    /// <summary>Color displayed by the Device when Ring State is No-Sessions.</summary>
    string NoSessionsColor { get; }
}

public sealed record SessionMapConfig(int StaleTimeoutMinutes, string NoSessionsColor = "#0044FF") : ISessionMapConfig;

internal sealed record SessionEntry(string Tool, SessionState State, DateTimeOffset LastSeen);

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
        var entry = new SessionEntry(evt.Tool, evt.State, _time.GetUtcNow());
        _sessions.AddOrUpdate(evt.SessionId, entry, (_, _) => entry);
    }

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>Removes sessions whose last-seen timestamp is older than <paramref name="staleTimeout"/>.</summary>
    public void EvictStale(TimeSpan staleTimeout)
    {
        var cutoff = _time.GetUtcNow() - staleTimeout;
        foreach (var (key, entry) in _sessions.ToArray())
        {
            if (entry.LastSeen < cutoff)
                _sessions.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Computes the highest-priority Ring State from all active sessions.
    /// Priority: Blocked > Working > Idle > NoSessions.
    /// </summary>
    public WireFrame ComputeRingState()
    {
        var sessions = _sessions.Values.ToList();

        if (sessions.Count == 0)
            return new WireFrame(RingState.NoSessions, string.Empty);

        var blocked = sessions.FirstOrDefault(s => s.State == SessionState.Blocked);
        if (blocked is not null)
            return new WireFrame(RingState.Blocked, blocked.Tool);

        var working = sessions.FirstOrDefault(s => s.State == SessionState.Working);
        if (working is not null)
            return new WireFrame(RingState.Working, working.Tool);

        var idle = sessions.First(s => s.State == SessionState.Idle);
        return new WireFrame(RingState.Idle, idle.Tool);
    }

    /// <summary>Returns a snapshot of all sessions (for the /status endpoint).</summary>
    public IReadOnlyList<SessionStatusEntry> GetAll() =>
        _sessions
            .Select(kvp => new SessionStatusEntry
            {
                SessionId = kvp.Key,
                Tool      = kvp.Value.Tool,
                State     = kvp.Value.State,
                LastSeen  = kvp.Value.LastSeen
            })
            .ToList()
            .AsReadOnly();
}
