using F75Studio.Lighting;

namespace F75Studio.Tests.TestSupport;

/// <summary>Creates fake captures that all draw from one shared result queue (a CapturedFrame, null, or an Exception to throw).</summary>
public sealed class FakeScreenCaptureFactory
{
    private readonly object _lock = new();
    private readonly Queue<object?> _results = new();
    private int _created, _disposed, _calls;

    /// <summary>Invoked at the start of every TryCapture call.</summary>
    public Action? OnCapture { get; set; }

    public int Created => Volatile.Read(ref _created);
    public int Disposed => Volatile.Read(ref _disposed);
    public int Calls => Volatile.Read(ref _calls);

    public FakeScreenCaptureFactory Enqueue(object? result)
    {
        lock (_lock) _results.Enqueue(result);
        return this;
    }

    public IScreenCapture Create()
    {
        Interlocked.Increment(ref _created);
        return new Capture(this);
    }

    private object? Next()
    {
        lock (_lock) return _results.Count > 0 ? _results.Dequeue() : null;
    }

    private sealed class Capture(FakeScreenCaptureFactory owner) : IScreenCapture
    {
        private int _isDisposed;

        public CapturedFrame? TryCapture(int timeoutMs)
        {
            Interlocked.Increment(ref owner._calls);
            owner.OnCapture?.Invoke();
            return owner.Next() switch
            {
                Exception ex => throw ex,
                CapturedFrame frame => frame,
                _ => null,
            };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0) Interlocked.Increment(ref owner._disposed);
        }
    }
}
