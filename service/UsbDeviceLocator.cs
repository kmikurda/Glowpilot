using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Glowpilot.Service;

/// <summary>A port entry with its OS-assigned port name and a device-identifier string.</summary>
public record PortDescriptor(string PortName, string DeviceId);

/// <summary>Platform-specific source of USB serial port descriptors.</summary>
public interface IPortDescriptorSource
{
    IEnumerable<PortDescriptor> GetDescriptors();
}

/// <summary>Locates the COM/tty port of a USB serial device by VID and PID.</summary>
public interface IUsbDeviceLocator
{
    /// <returns>Port name (e.g. "COM3" or "/dev/ttyUSB0"), or <see langword="null"/> if not found.</returns>
    string? FindPortName(ushort vid, ushort pid);
}

public sealed class UsbDeviceLocator : IUsbDeviceLocator
{
    private readonly IPortDescriptorSource _source;
    private readonly ILogger<UsbDeviceLocator> _logger;

    public UsbDeviceLocator(IPortDescriptorSource source, ILogger<UsbDeviceLocator> logger)
    {
        _source = source;
        _logger = logger;
    }

    public string? FindPortName(ushort vid, ushort pid)
    {
        // Synthesize the VID/PID marker used on both Windows (DeviceID) and Linux (synthesized).
        var marker = $"VID_{vid:X4}&PID_{pid:X4}";
        try
        {
            foreach (var desc in _source.GetDescriptors())
            {
                if (desc.DeviceId.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("USB device found on {Port}: {DeviceId}", desc.PortName, desc.DeviceId);
                    return desc.PortName;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error enumerating USB ports for VID={Vid:X4} PID={Pid:X4}", vid, pid);
        }
        return null;
    }
}

// ── Platform-specific port descriptor sources ─────────────────────────────────

/// <summary>
/// Windows WMI-based source. Queries <c>Win32_PnPEntity</c> for USB devices and
/// extracts the COM port number from the device name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWmiPortDescriptorSource : IPortDescriptorSource
{
    public IEnumerable<PortDescriptor> GetDescriptors()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT DeviceID, Name FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'");

        foreach (System.Management.ManagementObject obj in searcher.Get())
        {
            var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
            var name = obj["Name"]?.ToString() ?? string.Empty;

            // Name example: "USB Serial Device (COM3)"
            var parenOpen = name.IndexOf("(COM", StringComparison.OrdinalIgnoreCase);
            if (parenOpen < 0) continue;
            var parenClose = name.IndexOf(')', parenOpen);
            if (parenClose < 0) continue;

            var portName = name[(parenOpen + 1)..parenClose];
            yield return new PortDescriptor(portName, deviceId);
        }
    }
}

/// <summary>
/// Linux/macOS sysfs-based source. Reads <c>/sys/class/tty/*/device/idVendor</c> and
/// synthesises a Windows-style device-ID string so <see cref="UsbDeviceLocator"/> can
/// use the same matching logic on every platform.
/// </summary>
public sealed class UnixSysfsPortDescriptorSource : IPortDescriptorSource
{
    public IEnumerable<PortDescriptor> GetDescriptors()
    {
        const string ttyBase = "/sys/class/tty";
        if (!Directory.Exists(ttyBase))
            yield break;

        foreach (var ttyDir in Directory.EnumerateDirectories(ttyBase))
        {
            var deviceDir = Path.Combine(ttyDir, "device");
            var vendorFile = Path.Combine(deviceDir, "idVendor");
            var productFile = Path.Combine(deviceDir, "idProduct");

            if (!File.Exists(vendorFile) || !File.Exists(productFile))
                continue;

            var vendor = File.ReadAllText(vendorFile).Trim().ToUpperInvariant();
            var product = File.ReadAllText(productFile).Trim().ToUpperInvariant();
            var portName = "/dev/" + Path.GetFileName(ttyDir);

            // Synthesize a device ID in the same format used on Windows.
            var deviceId = $"USB\\VID_{vendor}&PID_{product}";
            yield return new PortDescriptor(portName, deviceId);
        }
    }
}
