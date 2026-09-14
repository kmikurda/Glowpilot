using Glowpilot.Shared;
using Microsoft.Extensions.Hosting;

namespace Glowpilot.Service;

/// <summary>
/// Orchestrates the core pipeline: receives Session Events, updates the Session Map,
/// drives the Device Writer, and runs background heartbeat and eviction timers.
/// </summary>
public sealed class SessionService : IHostedService, IDisposable
{
    private readonly SessionMap _map;
    private readonly DebounceWriter _writer;
    private readonly ISessionMapConfig _config;
    private Timer? _heartbeatTimer;
    private Timer? _evictionTimer;

    public SessionService(SessionMap map, DebounceWriter writer, ISessionMapConfig config)
    {
        _map = map;
        _writer = writer;
        _config = config;
    }

    // IHostedService ──────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _heartbeatTimer = new Timer(
            _ => WriteCurrentRingState(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        _evictionTimer = new Timer(
            _ => EvictStale(),
            null,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeTimers();
        return Task.CompletedTask;
    }

    // Public API ──────────────────────────────────────────────────────────────

    /// <summary>Processes an incoming Session Event and updates the Device.</summary>
    public void HandleEvent(SessionEvent evt)
    {
        if (evt.Closed)
            _map.Remove(evt.SessionId);
        else
            _map.Upsert(evt);

        _writer.Write(WithNoSessionsColor(_map.ComputeRingState()));
    }

    /// <summary>Evicts stale sessions and recomputes the Ring State.</summary>
    public void EvictStale()
    {
        _map.EvictStale(
            TimeSpan.FromMinutes(_config.StaleTimeoutMinutes),
            TimeSpan.FromSeconds(_config.IdleStaleTimeoutSeconds));
        _writer.Write(WithNoSessionsColor(_map.ComputeRingState()));
    }

    // Private ─────────────────────────────────────────────────────────────────

    /// <summary>Periodic write of the current Ring State — keeps the Device in sync
    /// even when no Session Events have arrived.</summary>
    private void WriteCurrentRingState() => _writer.Write(WithNoSessionsColor(_map.ComputeRingState()));

    /// <summary>
    /// Attaches the configured No-Sessions color to a NoSessions frame so the Device
    /// can render the correct slow-pulse color without hardcoding it in the firmware.
    /// Non-NoSessions frames are returned unchanged.
    /// </summary>
    private WireFrame WithNoSessionsColor(WireFrame frame) =>
        frame.State == RingState.NoSessions
            ? frame with { Color = _config.NoSessionsColor }
            : frame;

    private void DisposeTimers()
    {
        _heartbeatTimer?.Dispose();
        _evictionTimer?.Dispose();
    }

    public void Dispose() => DisposeTimers();
}
