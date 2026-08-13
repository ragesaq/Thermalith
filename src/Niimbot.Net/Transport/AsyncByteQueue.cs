namespace Niimbot.Net.Transport;

/// <summary>
/// Thread-safe byte pipe between a push-style source (BLE notifications arriving on a native
/// callback thread) and the pull-style <see cref="INiimbotTransport.ReadAsync"/> contract.
/// <see cref="Append"/> never blocks; <see cref="ReadAsync"/> waits up to a timeout for data and
/// returns 0 on an idle line, mirroring <see cref="SerialTransport"/> read semantics so
/// <see cref="NiimbotClient"/>'s pump behaves identically over both transports.
/// </summary>
internal sealed class AsyncByteQueue : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<byte[]> _segments = new();
    private int _offsetInHead;
    private SemaphoreSlim _available = new(0, int.MaxValue);
    private bool _disposed;

    /// <summary>Append a chunk (e.g. one BLE notification payload). Safe from any thread.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        lock (_lock)
        {
            if (_disposed)
                return;
            _segments.Enqueue(data.ToArray());
        }

        try
        {
            _available.Release();
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose; the bytes are moot.
        }
    }

    /// <summary>
    /// Read whatever is buffered into <paramref name="buffer"/>, waiting up to
    /// <paramref name="timeoutMs"/> for the first byte. Returns 0 on timeout, cancellation, or
    /// after <see cref="Dispose"/> — never throws for those cases (transport read contract).
    /// </summary>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, int timeoutMs, CancellationToken ct = default)
    {
        if (buffer.IsEmpty || _disposed)
            return 0;

        try
        {
            if (!await _available.WaitAsync(timeoutMs, ct).ConfigureAwait(false))
                return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }

        lock (_lock)
        {
            if (_disposed || _segments.Count == 0)
                return 0;

            var written = 0;
            var span = buffer.Span;
            while (written < span.Length && _segments.Count > 0)
            {
                var head = _segments.Peek();
                var take = Math.Min(span.Length - written, head.Length - _offsetInHead);
                head.AsSpan(_offsetInHead, take).CopyTo(span[written..]);
                written += take;
                _offsetInHead += take;
                if (_offsetInHead == head.Length)
                {
                    _segments.Dequeue();
                    _offsetInHead = 0;
                }
            }

            // We consumed one semaphore count but may have drained several segments (or left a
            // partial one). Re-balance so the count stays "≥1 iff bytes are buffered".
            RebalanceLocked();
            return written;
        }
    }

    /// <summary>Wake any waiting reader and drop buffered bytes. Idempotent.</summary>
    public void Dispose()
    {
        SemaphoreSlim available;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _segments.Clear();
            _offsetInHead = 0;
            available = _available;
        }

        // Release once so a parked reader wakes and observes _disposed, then dispose.
        try
        {
            available.Release();
        }
        catch (ObjectDisposedException)
        {
        }

        available.Dispose();
    }

    private void RebalanceLocked()
    {
        // Drain the semaphore to zero, then restore one count if data remains.
        while (_available.Wait(0))
        {
        }

        if (_segments.Count > 0)
            _available.Release();
    }
}
