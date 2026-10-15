using Glowpilot.Service;
using Glowpilot.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Glowpilot.Service.Tests;

/// <summary>Records every message logged to it, already formatted.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
    }
}

public class SessionServiceLoggingTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly MockDeviceWriter _device = new();
    private readonly DebounceWriter _writer;
    private readonly CapturingLogger<SessionService> _log = new();
    private readonly SessionService _service;

    public SessionServiceLoggingTests()
    {
        _writer = new DebounceWriter(_device);
        _service = new SessionService(
            new SessionMap(_time),
            _writer,
            new SessionMapConfig(StaleTimeoutMinutes: 10, IdleStaleTimeoutSeconds: 60),
            _log);
    }

    public void Dispose()
    {
        _service.Dispose();
        _writer.Dispose();
    }

    // ── Incoming events ───────────────────────────────────────────────────────

    [Fact]
    public void HandleEvent_LogsToolSessionAndState()
    {
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Working));

        Assert.Contains("claude session sess-1: Working", _log.Messages);
    }

    [Fact]
    public void HandleEvent_ClosedSession_LogsClosure()
    {
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Working));

        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Idle, Closed: true));

        Assert.Contains("claude session sess-1 closed", _log.Messages);
    }

    // ── Ring state transitions ────────────────────────────────────────────────

    [Fact]
    public void HandleEvent_FirstEvent_LogsRingStateTransitionFromNone()
    {
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Working));

        Assert.Contains(
            "Ring state: (none) -> Working (tool: claude, sessions: 1)",
            _log.Messages);
    }

    [Fact]
    public void HandleEvent_RingStateUnchanged_LogsTransitionOnlyOnce()
    {
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Working));
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Working));
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Working));

        Assert.Single(_log.Messages, m => m.StartsWith("Ring state:"));
    }

    [Fact]
    public void HandleEvent_RingStateChanges_LogsPreviousAndNewState()
    {
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Working));
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Idle));

        Assert.Contains(
            "Ring state: Working -> Idle (tool: claude, sessions: 1)",
            _log.Messages);
    }

    [Fact]
    public void RingStateNoSessions_LogsDashInsteadOfEmptyTool()
    {
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Working));

        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Idle, Closed: true));

        Assert.Contains(
            "Ring state: Working -> NoSessions (tool: -, sessions: 0)",
            _log.Messages);
    }

    // ── Eviction ──────────────────────────────────────────────────────────────

    [Fact]
    public void EvictStale_LogsEvictedSessionAndTheResultingRingState()
    {
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Idle));
        _time.Advance(TimeSpan.FromSeconds(90));

        _service.EvictStale();

        Assert.Contains(_log.Messages, m =>
            m.StartsWith("Evicted stale claude session sess-1 (was Idle, last seen "));
        Assert.Contains(
            "Ring state: Idle -> NoSessions (tool: -, sessions: 0)",
            _log.Messages);
    }

    [Fact]
    public void EvictStale_NothingStale_LogsNothing()
    {
        _service.HandleEvent(new SessionEvent("sess-1", "claude", SessionState.Working));
        var before = _log.Entries.Count;

        _service.EvictStale();

        Assert.Equal(before, _log.Entries.Count);
    }

    // ── Optional logger ───────────────────────────────────────────────────────

    [Fact]
    public void HandleEvent_WithoutLogger_DoesNotThrow()
    {
        using var writer = new DebounceWriter(new MockDeviceWriter());
        using var service = new SessionService(
            new SessionMap(_time), writer, new SessionMapConfig(StaleTimeoutMinutes: 10));

        var ex = Record.Exception(() =>
            service.HandleEvent(new SessionEvent("s", "claude", SessionState.Working)));

        Assert.Null(ex);
    }
}

public class TestHostLoggingTests
{
    [Fact]
    public void ServiceFactory_DoesNotAttachTheFileLogger()
    {
        // Guards against tests writing into the user's real service.log.
        using var factory = new ServiceFactory();

        var providers = factory.Services.GetServices<ILoggerProvider>();

        Assert.DoesNotContain(providers, p => p is FileLoggerProvider);
    }
}
