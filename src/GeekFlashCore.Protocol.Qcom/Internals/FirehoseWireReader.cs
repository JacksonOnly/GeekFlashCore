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

    internal bool StartupDataReceived { get; private set; }

    public FirehoseWireReader(ITransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public FirehoseResponse ReadResponse(int timeoutMilliseconds, CancellationToken cancellationToken = default,
        Action<FirehoseResponseLog>? publishLog = null)
    {
        ThrowIfDisposed();
        if (_queuedResponse is { } queued)
        {
            _queuedResponse = null;
            foreach (FirehoseResponseLog log in queued.Logs) publishLog?.Invoke(log);
            return queued;
        }
        long deadline = Stopwatch.GetTimestamp() + MillisecondsToTimestamp(timeoutMilliseconds);
        var logs = new List<FirehoseResponseLog>(4);
        int responseBytes = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> packet = ReadXmlPacket(GetRemainingMilliseconds(deadline), out bool rawMode);
            if (rawMode)
                throw new FirehoseProtocolException(Strings.Firehose_ResponseNotXml);
            AddPacketBudget(ref responseBytes, packet.Length);

            int firstLog = logs.Count;
            bool complete = FirehoseResponseParser.TryParsePacket(
                    packet,
                    logs,
                    out var status,
                    out rawMode,
                    out var attributes,
                    out var payloadElements);
            for (int index = firstLog; index < logs.Count; index++) publishLog?.Invoke(logs[index]);
            if (complete)
                return new FirehoseResponse(
                    logs,
                    attributes ?? new Dictionary<string, string>(),
                    status,
                    rawMode,
                    payloadElements);
        }
    }

    public FirehoseResponse ReadStartupLogs(int timeoutMilliseconds, Action<FirehoseResponseLog>? publishLog = null,
        int? probeRejectionTimeoutMilliseconds = null)
    {
        ThrowIfDisposed();
        StartupDataReceived = false;
        long deadline = Stopwatch.GetTimestamp() + MillisecondsToTimestamp(timeoutMilliseconds);
        var logs = new List<FirehoseResponseLog>(16);
        int responseBytes = 0;
        bool probeRejected = false;

        while (true)
        {
            ReadOnlySpan<byte> packet;
            bool rawMode;
            try { packet = ReadXmlPacket(GetRemainingMilliseconds(deadline), out rawMode); }
            catch (TimeoutException) when (probeRejected && IsXmlWhitespace(_xmlBuffer.WrittenSpan))
            {
                // Drain complete diagnostic packets and an optional NAK before confirming
                // with NOP. Never discard a partial packet or unfinished loader startup.
                return new FirehoseResponse(logs, new Dictionary<string, string>(), FirehoseResponseStatus.Nak, false);
            }
            finally { StartupDataReceived |= _xmlBuffer.WrittenCount > 0; }
            if (rawMode)
                throw new InvalidDataException(Strings.Firehose_StartupDataNotXml);
            AddPacketBudget(ref responseBytes, packet.Length);

            int firstLog = logs.Count;
            bool complete = FirehoseResponseParser.TryParsePacket(packet, logs, out _,
                out rawMode, out var attributes, out var elements);
            for (int index = firstLog; index < logs.Count; index++) publishLog?.Invoke(logs[index]);
            if (probeRejectionTimeoutMilliseconds is not null && complete && rawMode)
                throw new FirehoseProtocolException(Strings.Qcom_FirehoseRawModeUnexpected);
            if (logs.Exists(static log =>
                    log.Message.Contains("End of supported functions", StringComparison.Ordinal) ||
                    log.Message.Contains("VIP is enabled", StringComparison.OrdinalIgnoreCase)))
                return new FirehoseResponse(
                    logs,
                    new Dictionary<string, string>(),
                    FirehoseResponseStatus.Ack,
                    false);
            if (probeRejectionTimeoutMilliseconds is { } rejectionTimeout && !probeRejected &&
                logs.Exists(static log => log.Message.Contains("Failed to parse xml", StringComparison.OrdinalIgnoreCase)))
            {
                probeRejected = true;
                deadline = Math.Min(deadline, Stopwatch.GetTimestamp() + MillisecondsToTimestamp(rejectionTimeout));
            }
            if (probeRejected && complete && !rawMode)
                return new FirehoseResponse(logs, attributes!, FirehoseResponseStatus.Nak, false, elements);
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

    internal FirehoseResponse? ReadOptionalResponse(int timeoutMilliseconds, CancellationToken cancellationToken,
        Action<FirehoseResponseLog>? publishLog = null)
    {
        ThrowIfDisposed();
        long deadline = Stopwatch.GetTimestamp() + MillisecondsToTimestamp(timeoutMilliseconds);
        var logs = new List<FirehoseResponseLog>();
        int responseBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReadOnlySpan<byte> packet = ReadXmlPacket(GetRemainingMilliseconds(deadline), out bool raw);
                if (raw) throw new FirehoseProtocolException(Strings.Firehose_ResponseNotXml);
                AddPacketBudget(ref responseBytes, packet.Length);
                int firstLog = logs.Count;
                bool complete = FirehoseResponseParser.TryParsePacket(packet, logs, out var status, out raw,
                        out var attributes, out var elements);
                for (int index = firstLog; index < logs.Count; index++) publishLog?.Invoke(logs[index]);
                if (complete)
                    return new FirehoseResponse(logs, attributes!, status, raw, elements);
                _xmlBuffer.Clear();
            }
            catch (TimeoutException)
            {
                if (!IsXmlWhitespace(_xmlBuffer.WrittenSpan))
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

        while (IsXmlWhitespace(_xmlBuffer.WrittenSpan))
        {
            if (_xmlBuffer.WrittenCount >= FirehoseConstants.MaximumXmlPacketSize)
                throw new InvalidDataException(Strings.FormatFirehose_XmlPacketTooLarge(FirehoseConstants.MaximumXmlPacketSize));
            int count = Math.Min(32, FirehoseConstants.MaximumXmlPacketSize - _xmlBuffer.WrittenCount);
            int read = ReadSome(_xmlBuffer.GetSpan(count)[..count], GetRemainingMilliseconds(deadline));
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

    private static bool IsXmlWhitespace(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF) prefix = prefix[3..];
        foreach (byte value in prefix)
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        return true;
    }

    private static void AddPacketBudget(ref int consumed, int length)
    {
        consumed = checked(consumed + length);
        if (consumed > FirehoseConstants.MaximumXmlPacketSize)
            throw new InvalidDataException(Strings.FormatFirehose_XmlResponseTooLarge(FirehoseConstants.MaximumXmlPacketSize));
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
