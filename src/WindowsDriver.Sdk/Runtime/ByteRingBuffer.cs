using System.Collections.Concurrent;

namespace WindowsDriver.Sdk;

/// <summary>
/// Thread-safe byte ring buffer used by the audio and serial modules. Writers
/// drop bytes when the buffer is full (counted in <see cref="DroppedBytes"/>);
/// readers get whatever is available. This models a hardware FIFO between the
/// device and the host.
/// </summary>
public sealed class ByteRingBuffer
{
    private readonly object _gate = new();
    private readonly byte[] _buffer;
    private int _head; // next write position
    private int _tail; // next read position
    private int _count;

    public ByteRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }
        Capacity = capacity;
        _buffer = new byte[capacity];
    }

    public int Capacity { get; }
    public int Count { get { lock (_gate) { return _count; } } }
    public int FreeSpace { get { lock (_gate) { return Capacity - _count; } } }
    public long DroppedBytes { get; private set; }
    public long TotalBytesWritten { get; private set; }
    public long TotalBytesRead { get; private set; }

    /// <summary>
    /// Writes as much of the data as fits. Returns the number of bytes accepted;
    /// the remainder is dropped and counted.
    /// </summary>
    public int Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var accepted = Math.Min(data.Length, Capacity - _count);
            if (accepted == data.Length)
            {
                data.CopyTo(_buffer.AsSpan(_head));
            }
            else
            {
                data.Slice(0, accepted).CopyTo(_buffer.AsSpan(_head));
            }
            _head = (_head + accepted) % Capacity;
            _count += accepted;
            TotalBytesWritten += accepted;
            DroppedBytes += data.Length - accepted;
            return accepted;
        }
    }

    /// <summary>Reads up to the destination length. Returns the number of bytes read.</summary>
    public int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            var available = Math.Min(destination.Length, _count);
            if (available == 0)
            {
                return 0;
            }

            var firstChunk = Math.Min(available, Capacity - _tail);
            _buffer.AsSpan(_tail, firstChunk).CopyTo(destination);
            if (available > firstChunk)
            {
                _buffer.AsSpan(0, available - firstChunk).CopyTo(destination.Slice(firstChunk));
            }
            _tail = (_tail + available) % Capacity;
            _count -= available;
            TotalBytesRead += available;
            return available;
        }
    }

    /// <summary>Removes up to <paramref name="bytes"/> from the buffer without copying (drain).</summary>
    public int Skip(int bytes)
    {
        lock (_gate)
        {
            var skipped = Math.Min(bytes, _count);
            _tail = (_tail + skipped) % Capacity;
            _count -= skipped;
            TotalBytesRead += skipped;
            return skipped;
        }
    }

    /// <summary>Empties the buffer. Counters (<see cref="TotalBytesWritten"/> etc.) are preserved.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _head = 0;
            _tail = 0;
            _count = 0;
        }
    }
}
