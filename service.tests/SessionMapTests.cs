using Glowpilot.Service;
using Glowpilot.Shared;

namespace Glowpilot.Service.Tests;

public class SessionMapTests
{
    private static SessionMap MakeMap() => new SessionMap(new FakeTimeProvider());

    private static SessionEvent Evt(
        string id,
        SessionState state,
        string tool = "claude",
        bool closed = false) =>
        new(id, tool, state, closed);

    // ── ComputeRingState ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeRingState_NoSessions_ReturnsNoSessions()
    {
        var map = MakeMap();

        var frame = map.ComputeRingState();

        Assert.Equal(RingState.NoSessions, frame.State);
        Assert.Equal(string.Empty, frame.Tool);
    }

    [Fact]
    public void ComputeRingState_SingleIdleSession_ReturnsIdle()
    {
        var map = MakeMap();
        map.Upsert(Evt("A", SessionState.Idle));

        var frame = map.ComputeRingState();

        Assert.Equal(RingState.Idle, frame.State);
        Assert.Equal("claude", frame.Tool);
    }

    [Fact]
    public void ComputeRingState_WorkingAndIdle_ReturnsWorking()
    {
        var map = MakeMap();
        map.Upsert(Evt("A", SessionState.Idle));
        map.Upsert(Evt("B", SessionState.Working));

        var frame = map.ComputeRingState();

        Assert.Equal(RingState.Working, frame.State);
    }

    [Fact]
    public void ComputeRingState_BlockedAndWorking_ReturnsBlocked()
    {
        var map = MakeMap();
        map.Upsert(Evt("A", SessionState.Working));
        map.Upsert(Evt("B", SessionState.Blocked));

        var frame = map.ComputeRingState();

        Assert.Equal(RingState.Blocked, frame.State);
    }

    [Fact]
    public void ComputeRingState_ReturnsToolOfWinningSession()
    {
        var map = MakeMap();
        map.Upsert(Evt("A", SessionState.Blocked, "claude"));
        map.Upsert(Evt("B", SessionState.Working, "copilot"));

        var frame = map.ComputeRingState();

        Assert.Equal(RingState.Blocked, frame.State);
        Assert.Equal("claude", frame.Tool);
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_ExistingSession_RemovesIt()
    {
        var map = MakeMap();
        map.Upsert(Evt("A", SessionState.Blocked));

        map.Remove("A");

        Assert.Equal(RingState.NoSessions, map.ComputeRingState().State);
    }

    [Fact]
    public void Remove_NonExistentSession_DoesNotThrow()
    {
        var map = MakeMap();

        var ex = Record.Exception(() => map.Remove("does-not-exist"));

        Assert.Null(ex);
    }

    // ── EvictStale ────────────────────────────────────────────────────────────

    [Fact]
    public void EvictStale_SessionOlderThanTimeout_IsRemoved()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Blocked));

        // Advance 11 minutes past the 10-minute stale threshold
        fakeTime.Advance(TimeSpan.FromMinutes(11));

        map.EvictStale(TimeSpan.FromMinutes(10));

        Assert.Equal(RingState.NoSessions, map.ComputeRingState().State);
    }

    [Fact]
    public void EvictStale_RecentSession_IsKept()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Blocked));

        // Only 5 minutes pass — under the 10-minute threshold
        fakeTime.Advance(TimeSpan.FromMinutes(5));

        map.EvictStale(TimeSpan.FromMinutes(10));

        Assert.Equal(RingState.Blocked, map.ComputeRingState().State);
    }

    [Fact]
    public void EvictStale_MultipleSessionsMixedAge_EvictsOnlyOldOnes()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);

        map.Upsert(Evt("old", SessionState.Blocked));
        fakeTime.Advance(TimeSpan.FromMinutes(11));
        map.Upsert(Evt("new", SessionState.Working));

        map.EvictStale(TimeSpan.FromMinutes(10));

        // "old" is evicted, "new" remains
        var frame = map.ComputeRingState();
        Assert.Equal(RingState.Working, frame.State);
    }

    // ── EvictStale with per-state idle timeout ────────────────────────────────

    [Fact]
    public void EvictStale_IdleSessionPastIdleTimeout_IsEvicted()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Idle));

        fakeTime.Advance(TimeSpan.FromSeconds(90));

        map.EvictStale(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60));

        Assert.Equal(RingState.NoSessions, map.ComputeRingState().State);
    }

    [Fact]
    public void EvictStale_IdleSessionWithinIdleTimeout_IsKept()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Idle));

        fakeTime.Advance(TimeSpan.FromSeconds(30));

        map.EvictStale(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60));

        Assert.Equal(RingState.Idle, map.ComputeRingState().State);
    }

    [Fact]
    public void EvictStale_WorkingSessionPastIdleTimeoutButWithinDefault_IsKept()
    {
        // Working session outlives the idle timeout — only evicted by the default timeout.
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Working));

        fakeTime.Advance(TimeSpan.FromSeconds(90));

        map.EvictStale(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60));

        Assert.Equal(RingState.Working, map.ComputeRingState().State);
    }

}
