using System.Diagnostics;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Internals;

/// <summary>Replays protocol detection bytes and supplies a timeout to synchronous Sahara reads.</summary>
internal sealed class QcomSessionTransport(ITransport transport, byte[] prefix, int readTimeout) : ITransport
{
    private int _offset;
    private byte[]? _pending;
    private int _pendingOffset;
    public bool IsOpen => transport.IsOpen;
    public void Open() => transport.Open();
    public void Close() => transport.Close();
    public void Write(ReadOnlySpan<byte> data) => transport.Write(data);
    public void Write(byte[] data, int offset, int count) => transport.Write(data, offset, count);
    public int Read(Span<byte> data, int? timeoutInMilliseconds = null)
    {
        if (_pending is { } pending && _pendingOffset < pending.Length)
        {
            int pendingCount = Math.Min(data.Length, pending.Length - _pendingOffset);
            pending.AsSpan(_pendingOffset, pendingCount).CopyTo(data);
            _pendingOffset += pendingCount;
            if (_pendingOffset == pending.Length)
            {
                _pending = null;
                _pendingOffset = 0;
            }
            return pendingCount;
        }
        int count = Math.Min(data.Length, prefix.Length - _offset);
        if (count == 0) return transport.Read(data, timeoutInMilliseconds ?? readTimeout);
        prefix.AsSpan(_offset, count).CopyTo(data);
        _offset += count;
        return count;
    }
    public int Read(byte[] data, int offset, int count, int? timeoutInMilliseconds = null) => Read(data.AsSpan(offset, count), timeoutInMilliseconds);
    public int ReadExact(Span<byte> destination, int? timeoutInMilliseconds = null)
    {
        long started = Stopwatch.GetTimestamp();
        int timeout = timeoutInMilliseconds ?? readTimeout;
        int total = 0;
        while (total < destination.Length)
        {
            int remaining = timeout - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0) throw new TimeoutException();
            int read = Read(destination[total..], remaining);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
        return total;
    }
    public int ReadAvailable(Span<byte> data) =>
        _pending is not null || _offset < prefix.Length ? Read(data) : transport.ReadAvailable(data);
    public void Prepend(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        int remaining = _pending is null ? 0 : _pending.Length - _pendingOffset;
        byte[] combined = new byte[checked(data.Length + remaining)];
        data.CopyTo(combined);
        if (remaining > 0) _pending!.AsSpan(_pendingOffset, remaining).CopyTo(combined.AsSpan(data.Length));
        _pending = combined;
        _pendingOffset = 0;
    }
    public void Flush()
    {
        transport.Flush();
        DiscardBuffered();
    }
    internal void DiscardBuffered()
    {
        _offset = prefix.Length;
        _pending = null;
        _pendingOffset = 0;
    }
    public void Dispose() { }
}
