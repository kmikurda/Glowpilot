using System.Text.Json;
using Glowpilot.Shared;

namespace Glowpilot.Service;

/// <summary>Serialises a <see cref="WireFrame"/> to a newline-terminated JSON string.</summary>
public static class WireFrameSerializer
{
    public static string Serialize(WireFrame frame) =>
        JsonSerializer.Serialize(frame) + "\n";
}
