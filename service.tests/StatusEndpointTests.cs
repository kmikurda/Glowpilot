using System.Net.Http.Json;
using Glowpilot.Service;
using Glowpilot.Shared;

namespace Glowpilot.Service.Tests;

public class StatusEndpointTests : IDisposable
{
    private readonly ServiceFactory _factory;
    private readonly HttpClient _client;

    public StatusEndpointTests()
    {
        _factory = new ServiceFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ── No sessions ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_NoSessions_ReturnsEmptySessionsAndNoSessionsRingState()
    {
        var response = await _client.GetAsync("/status");

        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.NotNull(status);
        Assert.Empty(status.Sessions);
        Assert.Equal(RingState.NoSessions, status.RingState);
        Assert.False(status.DeviceConnected);
    }

    // ── Correct JSON after event ──────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_AfterBlockedEvent_ReturnsSessionAndRingState()
    {
        await _client.PostAsJsonAsync("/event", new SessionEvent("sess1", "claude", SessionState.Blocked));

        var response = await _client.GetAsync("/status");

        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.NotNull(status);
        Assert.Single(status.Sessions);
        var entry = status.Sessions[0];
        Assert.Equal("sess1", entry.SessionId);
        Assert.Equal("claude", entry.Tool);
        Assert.Equal(SessionState.Blocked, entry.State);
        Assert.Equal(RingState.Blocked, status.RingState);
    }

    // ── Localhost-accessible ──────────────────────────────────────────────────
    // The test client always connects via loopback; verifies the endpoint is
    // reachable from localhost (Kestrel is also configured to bind localhost-only).

    [Fact]
    public async Task GetStatus_FromLocalhost_ReturnsOk()
    {
        var response = await _client.GetAsync("/status");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
