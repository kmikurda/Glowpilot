using Glowpilot.Shared;

namespace Glowpilot.Service;

/// <summary>
/// Decorator that coalesces rapid <see cref="Write"/> calls and forwards at most
/// one frame per <see cref="DebounceMs"/> milliseconds to the underlying writer.
/// </summary>
public sealed class DebounceWriter : IDeviceWriter, IDisposable
{
    private readonly IDeviceWriter _inner;
    private WireFrame? _pending;
    private readonly object _lock = new();
    private readonly Timer _timer;

    public const int DebounceMs = 200;

    public DebounceWriter(IDeviceWriter inner)
    {
        _inner = inner;
        _timer = new Timer(Flush, null,
            TimeSpan.FromMilliseconds(DebounceMs),
            TimeSpan.FromMilliseconds(DebounceMs));
    }

    /// <summary>Updates the pending frame immediately; the timer emits it.</summary>
    public void Write(WireFrame frame)
    {
        lock (_lock) _pending = frame;
    }

    private void Flush(object? state)
    {
        WireFrame? toWrite;
        lock (_lock)
        {
            toWrite = _pending;
            _pending = null;
        }
        if (toWrite is not null)
            _inner.Write(toWrite);
    }

    public void Dispose() => _timer.Dispose();
}
