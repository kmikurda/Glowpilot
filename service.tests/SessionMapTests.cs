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

    // ── ComputeRingState with Blocked debounce ────────────────────────────────

    [Fact]
    public void ComputeRingState_BlockedWithinDebounce_ShowsWorking()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Blocked));

        // 1 second in — still within the 3-second debounce window
        fakeTime.Advance(TimeSpan.FromSeconds(1));

        var frame = map.ComputeRingState(blockedDebounce: TimeSpan.FromSeconds(3));

        Assert.Equal(RingState.Working, frame.State);
    }

    [Fact]
    public void ComputeRingState_BlockedPastDebounce_ShowsBlocked()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Blocked));

        fakeTime.Advance(TimeSpan.FromSeconds(4));

        var frame = map.ComputeRingState(blockedDebounce: TimeSpan.FromSeconds(3));

        Assert.Equal(RingState.Blocked, frame.State);
    }

    [Fact]
    public void ComputeRingState_BlockedNoDebounce_ShowsBlockedImmediately()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Blocked));

        var frame = map.ComputeRingState();

        Assert.Equal(RingState.Blocked, frame.State);
    }

    [Fact]
    public void ComputeRingState_BlockedDebouncing_WorkingSessionWins()
    {
        // Debounced Blocked + active Working → Working takes priority during debounce.
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Working));
        map.Upsert(Evt("B", SessionState.Blocked));

        fakeTime.Advance(TimeSpan.FromSeconds(1));

        var frame = map.ComputeRingState(blockedDebounce: TimeSpan.FromSeconds(3));

        Assert.Equal(RingState.Working, frame.State);
        Assert.Equal("claude", frame.Tool); // Working session wins
    }

    [Fact]
    public void ComputeRingState_StateSince_ResetOnStateChange()
    {
        // Confirm StateSince resets when state changes (not when same state re-upserted).
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Working));

        fakeTime.Advance(TimeSpan.FromSeconds(10));

        // Upsert the same state again — StateSince should NOT reset
        map.Upsert(Evt("A", SessionState.Working));

        // Now transition to Blocked — StateSince should reset to now (T+10s)
        map.Upsert(Evt("A", SessionState.Blocked));

        fakeTime.Advance(TimeSpan.FromSeconds(1));

        // 1s since Blocked was entered — still within debounce
        var frame = map.ComputeRingState(blockedDebounce: TimeSpan.FromSeconds(3));
        Assert.Equal(RingState.Working, frame.State);

        fakeTime.Advance(TimeSpan.FromSeconds(3));

        // Now 4s since Blocked was entered — past debounce
        frame = map.ComputeRingState(blockedDebounce: TimeSpan.FromSeconds(3));
        Assert.Equal(RingState.Blocked, frame.State);
    }

    // ── ComputeRingState with WorkingStale detection ──────────────────────────

    [Fact]
    public void ComputeRingState_WorkingSessionPastStaleTimeout_ReturnsWorkingStale()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Working));

        fakeTime.Advance(TimeSpan.FromSeconds(31));

        var frame = map.ComputeRingState(TimeSpan.FromSeconds(30));

        Assert.Equal(RingState.WorkingStale, frame.State);
    }

    [Fact]
    public void ComputeRingState_WorkingSessionWithinStaleTimeout_ReturnsWorking()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Working));

        fakeTime.Advance(TimeSpan.FromSeconds(15));

        var frame = map.ComputeRingState(TimeSpan.FromSeconds(30));

        Assert.Equal(RingState.Working, frame.State);
    }

    [Fact]
    public void ComputeRingState_WorkingSessionNoTimeout_ReturnsWorking()
    {
        // Without a stale timeout, Working never degrades to WorkingStale.
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Working));

        fakeTime.Advance(TimeSpan.FromMinutes(60));

        var frame = map.ComputeRingState();

        Assert.Equal(RingState.Working, frame.State);
    }

    [Fact]
    public void ComputeRingState_BlockedTakesPriorityOverWorkingStale()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Working));
        map.Upsert(Evt("B", SessionState.Blocked));

        fakeTime.Advance(TimeSpan.FromSeconds(31));

        var frame = map.ComputeRingState(TimeSpan.FromSeconds(30));

        Assert.Equal(RingState.Blocked, frame.State);
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

    // ── EvictStale reports what it removed ────────────────────────────────────

    [Fact]
    public void EvictStale_ReturnsOnlyTheEvictedSessions()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("stale", SessionState.Idle));
        fakeTime.Advance(TimeSpan.FromSeconds(90));
        map.Upsert(Evt("fresh", SessionState.Working));

        var evicted = map.EvictStale(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60));

        var entry = Assert.Single(evicted);
        Assert.Equal("stale", entry.SessionId);
        Assert.Equal("claude", entry.Tool);
        Assert.Equal(SessionState.Idle, entry.State);
    }

    [Fact]
    public void EvictStale_NothingStale_ReturnsEmpty()
    {
        var fakeTime = new FakeTimeProvider();
        var map = new SessionMap(fakeTime);
        map.Upsert(Evt("A", SessionState.Working));

        var evicted = map.EvictStale(TimeSpan.FromMinutes(10));

        Assert.Empty(evicted);
    }

}
