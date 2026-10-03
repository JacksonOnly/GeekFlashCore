using System.Buffers;
using System.Diagnostics;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Extensions;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal sealed class FirehoseWireReader : IDisposable
{
    private const int XmlProbeLength = 5;
    private static ReadOnlySpan<byte> DataEnd => "</data>"u8;

    private readonly ITransport _transport;
    private readonly ArrayBufferWriter<byte> _xmlBuffer = new(FirehoseConstants.InitialXmlBufferSize);
    private byte[]? _carry;
    private int _carryOffset;
    private int _carryCount;
    private FirehoseResponse? _queuedResponse;
    private bool _disposed;

    public FirehoseWireReader(ITransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public FirehoseResponse ReadResponse(int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_queuedResponse is { } queued)
        {
            _queuedResponse = null;
            return queued;
        }
        long deadline = Stopwatch.GetTimestamp() + MillisecondsToTimestamp(timeoutMilliseconds);
        var logs = new List<FirehoseResponseLog>(4);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> packet = ReadXmlPacket(GetRemainingMilliseconds(deadline), out bool rawMode);
            if (rawMode)
                throw new FirehoseProtocolException(Strings.Firehose_ResponseNotXml);

            if (FirehoseResponseParser.TryParsePacket(
                    packet,
                    logs,
                    out var status,
                    out rawMode,
                    out var attributes,
                    out var payloadElements))
                return new FirehoseResponse(
                    logs,
                    attributes ?? new Dictionary<string, string>(),
                    status,
                    rawMode,
                    payloadElements);
        }
    }

    public FirehoseResponse ReadStartupLogs(int timeoutMilliseconds)
    {
        ThrowIfDisposed();
        long deadline = Stopwatch.GetTimestamp() + MillisecondsToTimestamp(timeoutMilliseconds);
        var logs = new List<FirehoseResponseLog>(16);

        while (true)
        {
            ReadOnlySpan<byte> packet = ReadXmlPacket(GetRemainingMilliseconds(deadline), out bool rawMode);
            if (rawMode)
                throw new InvalidDataException(Strings.Firehose_StartupDataNotXml);

            FirehoseResponseParser.ParseLogs(packet, logs);
            if (logs.Exists(static log =>
                    log.Message.Contains("End of supported functions", StringComparison.Ordinal)))
                return new FirehoseResponse(
                    logs,
                    new Dictionary<string, string>(),
                    FirehoseResponseStatus.Ack,
                    false);
        }
    }

    public int ReadRaw(Span<byte> destination, int timeoutMilliseconds)
    {
        ThrowIfDisposed();
        int copied = CopyCarryTo(destination);
        if (copied > 0)
            return copied;
        return _transport.Read(destination, timeoutMilliseconds);
    }

    internal FirehoseResponse? ReadOptionalResponse(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        long deadline = Stopwatch.GetTimestamp() + MillisecondsToTimestamp(timeoutMilliseconds);
        var logs = new List<FirehoseResponseLog>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReadOnlySpan<byte> packet = ReadXmlPacket(GetRemainingMilliseconds(deadline), out bool raw);
                if (raw) throw new FirehoseProtocolException(Strings.Firehose_ResponseNotXml);
                if (FirehoseResponseParser.TryParsePacket(packet, logs, out var status, out raw,
                        out var attributes, out var elements))
                    return new FirehoseResponse(logs, attributes!, status, raw, elements);
                _xmlBuffer.Clear();
            }
            catch (TimeoutException)
            {
                if (_xmlBuffer.WrittenCount > 0)
                    throw new FirehoseProtocolException(Strings.Firehose_OptionalResponseIncomplete);
                return logs.Count == 0 ? null : new FirehoseResponse(logs,
                    new Dictionary<string, string>(), FirehoseResponseStatus.Ack, false);
            }
        }
    }

    internal void DiscardBuffered()
    {
        _queuedResponse = null;
        _xmlBuffer.Clear();
        ReturnCarry();
    }

    private ReadOnlySpan<byte> ReadXmlPacket(int timeoutMilliseconds, out bool rawMode)
    {
        _xmlBuffer.Clear();
        long deadline = Stopwatch.GetTimestamp() + MillisecondsToTimestamp(timeoutMilliseconds);

        while (_xmlBuffer.WrittenCount < XmlProbeLength)
        {
            Span<byte> prefix = _xmlBuffer.GetSpan(XmlProbeLength)[..(XmlProbeLength - _xmlBuffer.WrittenCount)];
            int read = ReadSome(prefix, GetRemainingMilliseconds(deadline));
            if (read <= 0) throw new EndOfStreamException(Strings.Firehose_TransportClosedReadingXml);
            _xmlBuffer.Advance(read);
        }

        if (!LooksLikeXml(_xmlBuffer.WrittenSpan))
        {
            PrependCarry(_xmlBuffer.WrittenSpan);
            rawMode = true;
            return ReadOnlySpan<byte>.Empty;
        }

        while (true)
        {
            int endIndex = _xmlBuffer.WrittenSpan.IndexOf(DataEnd);
            if (endIndex >= 0)
            {
                int packetLength = endIndex + DataEnd.Length;
                ReadOnlySpan<byte> overflow = _xmlBuffer.WrittenSpan[packetLength..];
                if (!overflow.IsEmpty)
                {
                    if (_transport is QcomSessionTransport sessionTransport)
                        sessionTransport.Prepend(overflow);
                    else
                        PrependCarry(overflow);
                }
                rawMode = false;
                return _xmlBuffer.WrittenSpan[..packetLength];
            }

            if (_xmlBuffer.WrittenCount >= FirehoseConstants.MaximumXmlPacketSize)
                throw new InvalidDataException(
                    Strings.FormatFirehose_XmlPacketTooLarge(FirehoseConstants.MaximumXmlPacketSize));

            int remainingCapacity = FirehoseConstants.MaximumXmlPacketSize - _xmlBuffer.WrittenCount;
            Span<byte> destination = _xmlBuffer.GetSpan(
                Math.Min(FirehoseConstants.InitialXmlBufferSize, remainingCapacity))[..Math.Min(
                    FirehoseConstants.InitialXmlBufferSize,
                    remainingCapacity)];
            int read = ReadSome(destination, GetRemainingMilliseconds(deadline));
            if (read <= 0)
                throw new EndOfStreamException(Strings.Firehose_TransportClosedReadingXml);
            _xmlBuffer.Advance(read);
        }
    }

    private void ReadExact(Span<byte> destination, int timeoutMilliseconds)
    {
        int copied = CopyCarryTo(destination);
        if (copied < destination.Length)
            _transport.ReadExact(destination[copied..], timeoutMilliseconds);
    }

    private int ReadSome(Span<byte> destination, int timeoutMilliseconds)
    {
        int copied = CopyCarryTo(destination);
        if (copied > 0)
            return copied;

        return _transport.Read(destination, timeoutMilliseconds);
    }

    private static bool LooksLikeXml(ReadOnlySpan<byte> prefix)
    {
        int offset = 0;
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF) offset = 3;
        while (offset < prefix.Length && prefix[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') offset++;
        return offset < prefix.Length && prefix[offset] == (byte)'<';
    }

    private int CopyCarryTo(Span<byte> destination)
    {
        int count = Math.Min(destination.Length, _carryCount);
        if (count == 0)
            return 0;

        _carry!.AsSpan(_carryOffset, count).CopyTo(destination);
        _carryOffset += count;
        _carryCount -= count;
        if (_carryCount == 0)
            _carryOffset = 0;
        return count;
    }

    private void PrependCarry(ReadOnlySpan<byte> data)
    {
        int combinedLength = checked(data.Length + _carryCount);
        if (combinedLength > FirehoseConstants.MaximumXmlPacketSize)
            throw new InvalidDataException(
                Strings.FormatFirehose_XmlPacketTooLarge(FirehoseConstants.MaximumXmlPacketSize));

        byte[] combined = ArrayPool<byte>.Shared.Rent(Math.Max(1, combinedLength));
        data.CopyTo(combined.AsSpan(0, data.Length));
        if (_carryCount > 0)
            _carry!.AsSpan(_carryOffset, _carryCount).CopyTo(combined.AsSpan(data.Length));
        ReturnCarry();
        _carry = combined;
        _carryOffset = 0;
        _carryCount = combinedLength;
    }

    private static long MillisecondsToTimestamp(int milliseconds)
    {
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        return (long)(milliseconds * (double)Stopwatch.Frequency / 1000d);
    }

    private static int GetRemainingMilliseconds(long deadline)
    {
        long ticks = deadline - Stopwatch.GetTimestamp();
        if (ticks <= 0)
            throw new TimeoutException(Strings.Firehose_ReadTimedOut);
        return Math.Max(1, (int)Math.Ceiling(ticks * 1000d / Stopwatch.Frequency));
    }

    // Probe only available bytes between outgoing raw blocks. Partial XML stays
    // in the same bounded carry buffer used by the blocking response reader.
    public FirehoseResponse? PollResponse()
    {
        ThrowIfDisposed();
        if (_queuedResponse is not null)
            return _queuedResponse;
        if (TryQueueCarryResponse())
            return _queuedResponse;

        Span<byte> available = stackalloc byte[FirehoseConstants.InitialXmlBufferSize];
        int read = _transport.ReadAvailable(available);
        if (read > 0)
        {
            AppendCarry(available[..read]);
            TryQueueCarryResponse();
        }
        return _queuedResponse;
    }

    private bool TryQueueCarryResponse()
    {
        if (_carryCount == 0)
            return false;
        ReadOnlySpan<byte> pending = _carry!.AsSpan(_carryOffset, _carryCount);
        int consumed = 0;
        List<FirehoseResponseLog>? logs = null;
        while (!pending.IsEmpty)
        {
            int end = pending.IndexOf(DataEnd);
            if (end < 0)
                return false;
            int length = end + DataEnd.Length;
            logs ??= new List<FirehoseResponseLog>();
            bool complete = FirehoseResponseParser.TryParsePacket(pending[..length], logs,
                out var status, out bool rawMode, out var attributes, out var payloadElements);
            consumed += length;
            if (complete)
            {
                _queuedResponse = new FirehoseResponse(logs,
                    attributes ?? new Dictionary<string, string>(), status, rawMode, payloadElements);
                _carryOffset += consumed;
                _carryCount -= consumed;
                if (_carryCount == 0) _carryOffset = 0;
                return true;
            }
            pending = pending[length..];
        }
        return false;
    }

    private void AppendCarry(ReadOnlySpan<byte> data)
    {
        int length = checked(_carryCount + data.Length);
        if (length > FirehoseConstants.MaximumXmlPacketSize)
            throw new InvalidDataException(Strings.FormatFirehose_XmlPacketTooLarge(FirehoseConstants.MaximumXmlPacketSize));
        if (_carry is null || _carry.Length < length)
        {
            byte[] next = ArrayPool<byte>.Shared.Rent(length);
            if (_carryCount > 0) _carry!.AsSpan(_carryOffset, _carryCount).CopyTo(next);
            if (_carry is not null) ArrayPool<byte>.Shared.Return(_carry);
            _carry = next;
        }
        else if (_carryOffset > 0)
            _carry.AsSpan(_carryOffset, _carryCount).CopyTo(_carry);
        data.CopyTo(_carry.AsSpan(_carryCount));
        _carryOffset = 0;
        _carryCount = length;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReturnCarry();
    }

    private void ReturnCarry()
    {
        if (_carry is not null)
            ArrayPool<byte>.Shared.Return(_carry);
        _carry = null;
        _carryOffset = 0;
        _carryCount = 0;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FirehoseWireReader));
    }
}
