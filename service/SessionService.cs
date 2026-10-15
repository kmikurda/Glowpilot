using Glowpilot.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger<SessionService> _logger;
    private WireFrame? _lastFrame;
    private Timer? _heartbeatTimer;
    private Timer? _evictionTimer;

    public SessionService(
        SessionMap map,
        DebounceWriter writer,
        ISessionMapConfig config,
        ILogger<SessionService>? logger = null)
    {
        _map = map;
        _writer = writer;
        _config = config;
        _logger = logger ?? NullLogger<SessionService>.Instance;
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
        {
            _logger.LogInformation("{Tool} session {SessionId} closed", evt.Tool, evt.SessionId);
            _map.Remove(evt.SessionId);
        }
        else
        {
            _logger.LogInformation("{Tool} session {SessionId}: {State}", evt.Tool, evt.SessionId, evt.State);
            _map.Upsert(evt);
        }

        WriteCurrentRingState();
    }

    /// <summary>Evicts stale sessions and recomputes the Ring State.</summary>
    public void EvictStale()
    {
        var evicted = _map.EvictStale(
            TimeSpan.FromMinutes(_config.StaleTimeoutMinutes),
            TimeSpan.FromSeconds(_config.IdleStaleTimeoutSeconds));

        foreach (var session in evicted)
        {
            _logger.LogInformation(
                "Evicted stale {Tool} session {SessionId} (was {State}, last seen {LastSeen:HH:mm:ss})",
                session.Tool, session.SessionId, session.State, session.LastSeen.ToLocalTime());
        }

        WriteCurrentRingState();
    }

    // Private ─────────────────────────────────────────────────────────────────

    private TimeSpan WorkingStaleTimeout  => TimeSpan.FromSeconds(_config.WorkingStaleTimeoutSeconds);
    private TimeSpan BlockedDebounceTimeout => TimeSpan.FromSeconds(_config.BlockedDebounceSeconds);

    /// <summary>Computes the current Ring State, logs it when it differs from the last one seen,
    /// and hands it to the Device Writer. Also the periodic heartbeat write — keeps the Device
    /// in sync even when no Session Events have arrived.</summary>
    private void WriteCurrentRingState()
    {
        var frame = WithNoSessionsColor(_map.ComputeRingState(WorkingStaleTimeout, BlockedDebounceTimeout));
        LogIfRingStateChanged(frame);
        _writer.Write(frame);
    }

    private void LogIfRingStateChanged(WireFrame frame)
    {
        var previous = Interlocked.Exchange(ref _lastFrame, frame);
        if (previous?.State == frame.State && previous.Tool == frame.Tool)
            return;

        _logger.LogInformation(
            "Ring state: {Previous} -> {State} (tool: {Tool}, sessions: {Sessions})",
            previous?.State.ToString() ?? "(none)",
            frame.State,
            string.IsNullOrEmpty(frame.Tool) ? "-" : frame.Tool,
            _map.GetAll().Count);
    }

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
