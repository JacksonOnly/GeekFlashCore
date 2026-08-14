using System.Buffers;
using System.Diagnostics;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Extensions;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal sealed class FirehoseWireReader
{
    private static ReadOnlySpan<byte> XmlPrefix => "<?xml"u8;
    private static ReadOnlySpan<byte> DataEnd => "</data>"u8;

    private readonly ITransport _transport;
    private readonly ArrayBufferWriter<byte> _xmlBuffer = new(FirehoseConstants.InitialXmlBufferSize);
    private byte[] _carry = [];
    private int _carryOffset;
    private int _carryCount;

    public FirehoseWireReader(ITransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public FirehoseResponse ReadResponse(int timeoutMilliseconds)
    {
        long deadline = Stopwatch.GetTimestamp() + MillisecondsToTimestamp(timeoutMilliseconds);
        var logs = new List<FirehoseResponseLog>(4);

        while (true)
        {
            ReadOnlySpan<byte> packet = ReadXmlPacket(GetRemainingMilliseconds(deadline), out bool rawMode);
            if (rawMode)
                return new FirehoseResponse(
                    logs,
                    new Dictionary<string, string>(),
                    FirehoseResponseStatus.Ack,
                    true);

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
        int copied = CopyCarryTo(destination);
        if (copied > 0)
            return copied;
        return _transport.Read(destination, timeoutMilliseconds);
    }

    private ReadOnlySpan<byte> ReadXmlPacket(int timeoutMilliseconds, out bool rawMode)
    {
        _xmlBuffer.Clear();
        long deadline = Stopwatch.GetTimestamp() + MillisecondsToTimestamp(timeoutMilliseconds);

        Span<byte> prefix = _xmlBuffer.GetSpan(XmlPrefix.Length)[..XmlPrefix.Length];
        ReadExact(prefix, GetRemainingMilliseconds(deadline));
        _xmlBuffer.Advance(prefix.Length);

        if (!_xmlBuffer.WrittenSpan.StartsWith(XmlPrefix))
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
                    PrependCarry(overflow);
                rawMode = false;
                return _xmlBuffer.WrittenSpan[..packetLength];
            }

            if (_xmlBuffer.WrittenCount >= FirehoseConstants.MaximumXmlPacketSize)
                throw new InvalidDataException(
                    Strings.FormatFirehose_XmlPacketTooLarge(FirehoseConstants.MaximumXmlPacketSize));

            Span<byte> destination = _xmlBuffer.GetSpan(FirehoseConstants.InitialXmlBufferSize);
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
        return copied > 0 ? copied : _transport.Read(destination, timeoutMilliseconds);
    }

    private int CopyCarryTo(Span<byte> destination)
    {
        int count = Math.Min(destination.Length, _carryCount);
        if (count == 0)
            return 0;

        _carry.AsSpan(_carryOffset, count).CopyTo(destination);
        _carryOffset += count;
        _carryCount -= count;
        if (_carryCount == 0)
            _carryOffset = 0;
        return count;
    }

    private void PrependCarry(ReadOnlySpan<byte> data)
    {
        byte[] combined = new byte[data.Length + _carryCount];
        data.CopyTo(combined);
        if (_carryCount > 0)
            _carry.AsSpan(_carryOffset, _carryCount).CopyTo(combined.AsSpan(data.Length));
        _carry = combined;
        _carryOffset = 0;
        _carryCount = combined.Length;
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
}
