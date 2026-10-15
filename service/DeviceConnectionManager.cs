using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Glowpilot.Service;

/// <summary>
/// Background service that polls for a Glowpilot USB device (VID=0x2886 PID=0x0045),
/// opens the serial connection when the device appears, performs the firmware version
/// handshake, and closes the connection when the device is removed.
/// All exceptions are caught and logged — this service never crashes the host.
/// </summary>
public sealed class DeviceConnectionManager : BackgroundService
{
    // XIAO ESP32-C3 uses the Espressif hardware USB JTAG/Serial controller whose
    // VID/PID are fixed in silicon and cannot be overridden by firmware build flags.
    private const ushort VendorId  = 0x303A;   // Espressif Systems
    private const ushort ProductId = 0x1001;

    private readonly IUsbDeviceLocator _locator;
    private readonly ISerialDeviceConnection _connection;
    private readonly ILogger<DeviceConnectionManager> _logger;
    private readonly TimeSpan _pollInterval;
    private string? _connectedPort;

    public DeviceConnectionManager(
        IUsbDeviceLocator locator,
        ISerialDeviceConnection connection,
        ILogger<DeviceConnectionManager> logger,
        TimeSpan? pollInterval = null)
    {
        _locator      = locator;
        _connection   = connection;
        _logger       = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DeviceConnectionManager started — polling every {Interval:N1}s for VID={Vid:X4} PID={Pid:X4}",
            _pollInterval.TotalSeconds, VendorId, ProductId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unexpected error in DeviceConnectionManager poll loop");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _connection.Disconnect();
        _logger.LogInformation("DeviceConnectionManager stopped");
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var portName = _locator.FindPortName(VendorId, ProductId);

        if (portName is not null && _connectedPort is null)
        {
            // New device detected — open port and handshake.
            _logger.LogInformation("USB device detected on {Port}", portName);
            await ConnectAndHandshakeAsync(portName, cancellationToken);
        }
        else if (portName is null && _connectedPort is not null)
        {
            // Device removed.
            _logger.LogInformation("USB device removed (was on {Port})", _connectedPort);
            _connection.Disconnect();
            _connectedPort = null;
        }
        else if (_connectedPort is not null && !_connection.IsConnected)
        {
            // Connection was lost mid-session (e.g. IOException) — attempt reconnect.
            _logger.LogInformation("Connection to {Port} lost — reconnecting", _connectedPort);
            await ConnectAndHandshakeAsync(_connectedPort, cancellationToken);
        }
    }

    private async Task ConnectAndHandshakeAsync(string portName, CancellationToken cancellationToken)
    {
        _connection.Connect(portName);
        if (_connection.IsConnected)
        {
            _connectedPort = portName;
            await _connection.PerformHandshakeAsync(cancellationToken);
        }
    }
}
