using System.Net.Http.Json;
using Glowpilot.Service;
using Glowpilot.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Glowpilot.Service.Tests;

// ── Test infrastructure ───────────────────────────────────────────────────────

public sealed class ServiceFactory : WebApplicationFactory<Program>
{
    public MockDeviceWriter MockWriter { get; } = new();
    public FakeTimeProvider FakeTime { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Program.cs attaches the file logger; a test host must not write into the real log
            // file that an installed Service may be using.
            services.AddLogging(logging => logging.ClearProviders());

            // Replace production TimeProvider with controllable fake
            ReplaceDescriptor<TimeProvider>(services, _ => FakeTime);

            // Replace SerialDeviceWriter (IDeviceWriter) with mock
            ReplaceDescriptor<IDeviceWriter>(services, _ => MockWriter);
        });
    }

    private static void ReplaceDescriptor<T>(
        IServiceCollection services,
        Func<IServiceProvider, T> factory) where T : class
    {
        var existing = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        if (existing is not null)
            services.Remove(existing);
        services.AddSingleton<T>(factory);
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

public class SessionPipelineTests : IDisposable
{
    private readonly ServiceFactory _factory;
    private readonly HttpClient _client;

    public SessionPipelineTests()
    {
        _factory = new ServiceFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private Task<System.Net.Http.HttpResponseMessage> PostEvent(SessionEvent evt) =>
        _client.PostAsJsonAsync("/event", evt);

    // Wait long enough for the 200 ms debounce window to flush
    private static Task WaitForDebounce() => Task.Delay(350);

    // ── 1. Priority test ─────────────────────────────────────────────────────

    [Fact]
    public async Task Priority_BlockedThenWorking_MockReceivesBlocked()
    {
        await PostEvent(new SessionEvent("A", "claude", SessionState.Blocked));
        await PostEvent(new SessionEvent("B", "claude", SessionState.Working));

        // Advance past the 3-second Blocked debounce window, then post another event
        // to trigger a ring-state recompute with the updated clock.
        _factory.FakeTime.Advance(TimeSpan.FromSeconds(4));
        await PostEvent(new SessionEvent("B", "claude", SessionState.Working));

        await WaitForDebounce();

        var lastWrite = _factory.MockWriter.LastWrite;
        Assert.NotNull(lastWrite);
        Assert.Equal(RingState.Blocked, lastWrite.State);
    }

    // ── 2. Closed test ───────────────────────────────────────────────────────

    [Fact]
    public async Task Closed_BlockedSessionClosed_WorkingBecomesRingState()
    {
        await PostEvent(new SessionEvent("A", "claude", SessionState.Blocked));
        await PostEvent(new SessionEvent("B", "claude", SessionState.Working));
        await PostEvent(new SessionEvent("A", "claude", SessionState.Idle, Closed: true));

        await WaitForDebounce();

        var lastWrite = _factory.MockWriter.LastWrite;
        Assert.NotNull(lastWrite);
        Assert.Equal(RingState.Working, lastWrite.State);
    }

    // ── 3. Copilot regression: session must stay Idle after agent:done ───────
    // Root cause: agent:done was mapped to (Idle, Closed:true), which removed
    // the session immediately, causing NoSessions (blue) instead of Idle (green).
    // Fixed by mapping to (Idle, Closed:false) so the session persists as Idle.

    [Fact]
    public async Task Idle_WorkingSessionTransitionsToIdle_MockReceivesIdle()
    {
        await PostEvent(new SessionEvent("copilot", "copilot", SessionState.Working));
        await PostEvent(new SessionEvent("copilot", "copilot", SessionState.Idle));

        await WaitForDebounce();

        var lastWrite = _factory.MockWriter.LastWrite;
        Assert.NotNull(lastWrite);
        Assert.Equal(RingState.Idle, lastWrite.State);
    }

    // ── 5. NoSessions test ───────────────────────────────────────────────────

    [Fact]
    public async Task NoSessions_AfterAllSessionsEvicted_MockReceivesNoSessions()
    {
        await PostEvent(new SessionEvent("A", "claude", SessionState.Idle));

        // Advance past stale threshold and trigger eviction
        _factory.FakeTime.Advance(TimeSpan.FromMinutes(11));
        var service = _factory.Services.GetRequiredService<SessionService>();
        service.EvictStale();

        await WaitForDebounce();

        var lastWrite = _factory.MockWriter.LastWrite;
        Assert.NotNull(lastWrite);
        Assert.Equal(RingState.NoSessions, lastWrite.State);
    }

    // ── 6. Debounce test ─────────────────────────────────────────────────────

    [Fact]
    public async Task Debounce_TenRapidPosts_MockReceivesAtMostTwoWrites()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(i => PostEvent(new SessionEvent($"sess-{i}", "claude", SessionState.Working)));

        // Fire all 10 requests as close together as possible
        await Task.WhenAll(tasks);

        await WaitForDebounce();

        // All 10 go through DebounceWriter which coalesces to at most 1 per 200 ms window;
        // we allow 2 to tolerate a timer boundary edge case.
        Assert.True(
            _factory.MockWriter.WriteCount <= 2,
            $"Expected ≤ 2 writes but got {_factory.MockWriter.WriteCount}.");
    }

    // ── 7. Eviction test ─────────────────────────────────────────────────────
    // Verifies that only the stale session is evicted and Ring State recomputes
    // from the remaining sessions rather than jumping straight to NoSessions.

    [Fact]
    public async Task Eviction_StaleBlockedEvicted_WorkingSessionBecomesRingState()
    {
        // Session A arrives first — becomes stale after the time advance
        await PostEvent(new SessionEvent("A", "claude", SessionState.Blocked));

        // Advance past stale threshold
        _factory.FakeTime.Advance(TimeSpan.FromMinutes(11));

        // Session B arrives *after* the time advance — it is fresh
        await PostEvent(new SessionEvent("B", "claude", SessionState.Working));

        // Evict: A (stale) should be removed; B (fresh) should remain
        var service = _factory.Services.GetRequiredService<SessionService>();
        service.EvictStale();

        await WaitForDebounce();

        var lastWrite = _factory.MockWriter.LastWrite;
        Assert.NotNull(lastWrite);
        // A (blocked) evicted → only B (working) remains → ring state = Working
        Assert.Equal(RingState.Working, lastWrite.State);
    }
}
