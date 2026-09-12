using Glowpilot.Shared;

namespace Glowpilot.Service;

public interface IDeviceWriter
{
    void Write(WireFrame frame);

    /// <summary>Whether the underlying Device is currently connected.</summary>
    bool IsConnected { get; }
}

/// <summary>
/// Stub implementation — real USB serial write is tracked in issue #7.
/// When implemented, use <see cref="WireFrameSerializer.Serialize"/> to produce the
/// newline-delimited JSON string before sending it over the <see cref="System.IO.Ports.SerialPort"/>.
/// </summary>
public sealed class SerialDeviceWriter : IDeviceWriter
{
    /// <summary>Always false until USB serial is implemented (issue #7).</summary>
    public bool IsConnected => false;

    public void Write(WireFrame frame)
    {
        throw new NotImplementedException("USB serial write is not yet implemented (issue #7).");
    }
}
