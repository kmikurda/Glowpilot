using Glowpilot.Shared;

namespace Glowpilot.Service;

public interface IDeviceWriter
{
    void Write(WireFrame frame);
}

/// <summary>
/// Stub implementation — real USB serial write is tracked in issue #7.
/// When implemented, use <see cref="WireFrameSerializer.Serialize"/> to produce the
/// newline-delimited JSON string before sending it over the <see cref="System.IO.Ports.SerialPort"/>.
/// </summary>
public sealed class SerialDeviceWriter : IDeviceWriter
{
    public void Write(WireFrame frame)
    {
        throw new NotImplementedException("USB serial write is not yet implemented (issue #7).");
    }
}
