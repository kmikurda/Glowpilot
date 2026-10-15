using Glowpilot.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glowpilot.Service.Tests;

public class DeviceConnectionManagerTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan WaitTime = TimeSpan.FromMilliseconds(200);

    private static DeviceConnectionManager Create(
        IUsbDeviceLocator locator,
        ISerialDeviceConnection connection) =>
        new(locator, connection, NullLogger<DeviceConnectionManager>.Instance, FastPoll);

    // ── 1. Device found on first poll → Connect + Handshake called ───────────

    [Fact]
    public async Task DeviceDetected_ConnectAndHandshakeAreCalled()
    {
        var locator = new StubUsbDeviceLocator("COM3");
        var connection = new SpySerialDeviceConnection();
        var manager = Create(locator, connection);

        using var cts = new CancellationTokenSource();
        await manager.StartAsync(cts.Token);
        await Task.Delay(WaitTime);
        cts.Cancel();
        await manager.StopAsync(CancellationToken.None);

        Assert.True(connection.ConnectCalled, "Connect should be called when device is found");
        Assert.Equal("COM3", connection.ConnectedPort);
        Assert.True(connection.HandshakeCalled, "Handshake should be called after Connect");
    }

    // ── 2. Device disappears → Disconnect called ─────────────────────────────

    [Fact]
    public async Task DeviceRemoved_DisconnectIsCalled()
    {
        var locator = new StubUsbDeviceLocator("COM3");
        var connection = new SpySerialDeviceConnection();
        var manager = Create(locator, connection);

        using var cts = new CancellationTokenSource();
        await manager.StartAsync(cts.Token);

        // Let the first poll connect
        await Task.Delay(WaitTime);

        // Remove the device
        locator.PortName = null;

        // Let the next poll detect removal
        await Task.Delay(WaitTime);

        cts.Cancel();
        await manager.StopAsync(CancellationToken.None);

        Assert.True(connection.DisconnectCalled, "Disconnect should be called when device disappears");
    }

    // ── 3. No device at all → no connection attempts ─────────────────────────

    [Fact]
    public async Task NoDevicePresent_ConnectIsNeverCalled()
    {
        var locator = new StubUsbDeviceLocator(null);
        var connection = new SpySerialDeviceConnection();
        var manager = Create(locator, connection);

        using var cts = new CancellationTokenSource();
        await manager.StartAsync(cts.Token);
        await Task.Delay(WaitTime);
        cts.Cancel();
        await manager.StopAsync(CancellationToken.None);

        Assert.False(connection.ConnectCalled, "Connect should not be called if no device is present");
    }

    // ── 4. Connection dropped (IsConnected=false) → reconnect attempted ──────

    [Fact]
    public async Task ConnectionLost_ReconnectIsAttempted()
    {
        var locator = new StubUsbDeviceLocator("COM3");
        var connection = new SpySerialDeviceConnection();
        var manager = Create(locator, connection);

        using var cts = new CancellationTokenSource();
        await manager.StartAsync(cts.Token);
        await Task.Delay(WaitTime);

        // Simulate connection drop
        connection.SimulateDrop();
        int connectCountBeforeDrop = connection.ConnectCallCount;

        await Task.Delay(WaitTime);
        cts.Cancel();
        await manager.StopAsync(CancellationToken.None);

        Assert.True(connection.ConnectCallCount > connectCountBeforeDrop,
            "Connect should be retried after connection is lost");
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

file sealed class StubUsbDeviceLocator : IUsbDeviceLocator
{
    public string? PortName { get; set; }

    public StubUsbDeviceLocator(string? portName) => PortName = portName;

    public string? FindPortName(ushort vid, ushort pid) => PortName;
}

file sealed class SpySerialDeviceConnection : ISerialDeviceConnection
{
    public bool IsConnected { get; private set; }
    public bool ConnectCalled => ConnectCallCount > 0;
    public int ConnectCallCount { get; private set; }
    public bool DisconnectCalled { get; private set; }
    public bool HandshakeCalled { get; private set; }
    public string? ConnectedPort { get; private set; }

    public void Connect(string portName)
    {
        ConnectCallCount++;
        ConnectedPort = portName;
        IsConnected = true;
    }

    public void Disconnect()
    {
        DisconnectCalled = true;
        IsConnected = false;
    }

    public Task<bool> PerformHandshakeAsync(CancellationToken cancellationToken = default)
    {
        HandshakeCalled = true;
        return Task.FromResult(true);
    }

    /// <summary>Simulates an unexpected connection drop (e.g. IOException).</summary>
    public void SimulateDrop() => IsConnected = false;
}
