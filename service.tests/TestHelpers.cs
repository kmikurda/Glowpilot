using System.Collections.Concurrent;
using Glowpilot.Service;
using Glowpilot.Shared;

namespace Glowpilot.Service.Tests;

/// <summary>
/// Controllable <see cref="TimeProvider"/> substitute for deterministic time-dependent tests.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public void Advance(TimeSpan delta) => _now += delta;

    public void SetUtcNow(DateTimeOffset value) => _now = value;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// Thread-safe spy that records every frame written to it.
/// </summary>
public sealed class MockDeviceWriter : IDeviceWriter
{
    public bool IsConnected => false;

    private readonly ConcurrentQueue<WireFrame> _writes = new();
    private WireFrame? _lastWrite;

    public void Write(WireFrame frame)
    {
        _writes.Enqueue(frame);
        Interlocked.Exchange(ref _lastWrite, frame);
    }

    /// <summary>The most recently written frame; O(1).</summary>
    public WireFrame? LastWrite => Volatile.Read(ref _lastWrite);

    public int WriteCount => _writes.Count;

    public IReadOnlyList<WireFrame> AllWrites => _writes.ToList();
}
