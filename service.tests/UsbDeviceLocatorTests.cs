using Glowpilot.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glowpilot.Service.Tests;

public class UsbDeviceLocatorTests
{
    private static UsbDeviceLocator CreateLocator(params PortDescriptor[] descriptors)
    {
        var source = new StubPortDescriptorSource(descriptors);
        return new UsbDeviceLocator(source, NullLogger<UsbDeviceLocator>.Instance);
    }

    [Fact]
    public void FindPortName_MatchingVidPid_ReturnsPortName()
    {
        var locator = CreateLocator(
            new PortDescriptor("COM3", "USB\\VID_2886&PID_0045&MI_00\\..."),
            new PortDescriptor("COM4", "USB\\VID_1234&PID_5678&MI_00\\..."));

        var result = locator.FindPortName(0x2886, 0x0045);

        Assert.Equal("COM3", result);
    }

    [Fact]
    public void FindPortName_OnlyOtherVidPid_ReturnsNull()
    {
        var locator = CreateLocator(
            new PortDescriptor("COM4", "USB\\VID_1234&PID_5678&MI_00\\..."));

        var result = locator.FindPortName(0x2886, 0x0045);

        Assert.Null(result);
    }

    [Fact]
    public void FindPortName_EmptyList_ReturnsNull()
    {
        var locator = CreateLocator();

        var result = locator.FindPortName(0x2886, 0x0045);

        Assert.Null(result);
    }

    [Fact]
    public void FindPortName_CaseInsensitiveMatch_ReturnsPort()
    {
        // On Linux, idVendor / idProduct come back in lowercase
        var locator = CreateLocator(
            new PortDescriptor("/dev/ttyUSB0", "USB\\vid_2886&pid_0045"));

        var result = locator.FindPortName(0x2886, 0x0045);

        Assert.Equal("/dev/ttyUSB0", result);
    }

    [Fact]
    public void FindPortName_MultipleDevices_ReturnsFirstMatch()
    {
        var locator = CreateLocator(
            new PortDescriptor("COM5", "USB\\VID_AAAA&PID_BBBB"),
            new PortDescriptor("COM6", "USB\\VID_2886&PID_0045"),
            new PortDescriptor("COM7", "USB\\VID_2886&PID_0045"));

        var result = locator.FindPortName(0x2886, 0x0045);

        Assert.Equal("COM6", result);
    }

    [Fact]
    public void FindPortName_SourceThrows_ReturnsNull()
    {
        var source = new ThrowingPortDescriptorSource();
        var locator = new UsbDeviceLocator(source, NullLogger<UsbDeviceLocator>.Instance);

        // Should not propagate the exception
        var result = locator.FindPortName(0x2886, 0x0045);

        Assert.Null(result);
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

file sealed class StubPortDescriptorSource : IPortDescriptorSource
{
    private readonly PortDescriptor[] _descriptors;

    public StubPortDescriptorSource(PortDescriptor[] descriptors) => _descriptors = descriptors;

    public IEnumerable<PortDescriptor> GetDescriptors() => _descriptors;
}

file sealed class ThrowingPortDescriptorSource : IPortDescriptorSource
{
    public IEnumerable<PortDescriptor> GetDescriptors() =>
        throw new InvalidOperationException("Simulated WMI failure");
}
