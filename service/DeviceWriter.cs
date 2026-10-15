using System.IO.Ports;
using System.Text.Json;
using Glowpilot.Shared;
using Microsoft.Extensions.Logging;

namespace Glowpilot.Service;

public interface IDeviceWriter
{
    void Write(WireFrame frame);

    /// <summary>Whether the underlying Device is currently connected.</summary>
    bool IsConnected { get; }
}

/// <summary>
/// Contract for managing the lifecycle of the serial device connection.
/// Implemented by <see cref="SerialDeviceWriter"/>; exposed as a separate interface so
/// <see cref="DeviceConnectionManager"/> can control the port without depending on the
/// concrete type.
/// </summary>
public interface ISerialDeviceConnection
{
    bool IsConnected { get; }
    void Connect(string portName);
    void Disconnect();
    Task<bool> PerformHandshakeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes <see cref="WireFrame"/> values as newline-delimited JSON to a USB serial device.
/// When no device is connected, writes are silently dropped (no-op, no exception).
/// Implements both <see cref="IDeviceWriter"/> and <see cref="ISerialDeviceConnection"/> so
/// a single singleton can be shared by both the session pipeline and the connection manager.
/// </summary>
public sealed class SerialDeviceWriter : IDeviceWriter, ISerialDeviceConnection
{
    private readonly ILogger<SerialDeviceWriter> _logger;
    private SerialPort? _port;
    private volatile bool _connected;
    private readonly object _lock = new();

    public SerialDeviceWriter(ILogger<SerialDeviceWriter> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _connected;

    // ISerialDeviceConnection ─────────────────────────────────────────────────

    public void Connect(string portName)
    {
        lock (_lock)
        {
            ClosePort();
            try
            {
                var sp = new SerialPort(portName, 115200)
                {
                    NewLine = "\n",
                    ReadTimeout = 2000,
                    WriteTimeout = 2000,
                };
                sp.Open();
                _port = sp;
                _connected = true;
                _logger.LogInformation("Device connected on {Port}", portName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not open serial port {Port}", portName);
                ClosePort();
            }
        }
    }

    public void Disconnect()
    {
        lock (_lock) ClosePort();
    }

    public async Task<bool> PerformHandshakeAsync(CancellationToken cancellationToken = default)
    {
        SerialPort? port;
        lock (_lock) port = _port;

        if (port is null || !_connected) return false;

        try
        {
            const string greeting = "{\"v\":1,\"name\":\"glowpilot-service\"}";
            port.WriteLine(greeting);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            string? response = await Task.Run(() =>
            {
                try   { return port.ReadLine(); }
                catch (TimeoutException) { return null; }
            }, cts.Token).ConfigureAwait(false);

            if (response is null)
            {
                _logger.LogWarning("Handshake timed out — continuing in degraded mode");
                return false;
            }

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            var fw = root.TryGetProperty("fw", out var fwProp) ? fwProp.GetString() : "unknown";

            if (ok)
                _logger.LogInformation("Handshake OK — firmware {FwVersion}", fw);
            else
                _logger.LogWarning("Handshake returned ok=false — continuing in degraded mode");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handshake failed — continuing in degraded mode");
            return false;
        }
    }

    // IDeviceWriter ───────────────────────────────────────────────────────────

    public void Write(WireFrame frame)
    {
        if (!_connected) return;   // fast path — no lock needed for the check

        lock (_lock)
        {
            if (!_connected || _port is null) return;
            try
            {
                _port.Write(WireFrameSerializer.Serialize(frame));
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IOException on serial write — marking disconnected");
                _connected = false;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Serial port closed unexpectedly — marking disconnected");
                _connected = false;
            }
        }
    }

    // Private ─────────────────────────────────────────────────────────────────

    private void ClosePort()
    {
        if (_port is not null)
        {
            try { _port.Close(); } catch { /* best-effort */ }
            _port.Dispose();
            _port = null;
        }
        _connected = false;
    }
}
