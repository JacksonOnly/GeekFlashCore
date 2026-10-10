using System.Diagnostics;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Sprd.Internals;

internal readonly record struct SprdResponse(ushort Type, ReadOnlyMemory<byte> Data, bool UseCrc);

/// <summary>One bounded synchronous request/response stream. Returned memory expires on the next command.</summary>
internal sealed class SprdWire : IDisposable
{
    private readonly ITransport _transport;
    private readonly SprdProtocolOptions _options;
    private readonly byte[] _body;
    private readonly byte[] _encoded;
    private readonly byte[] _input = new byte[8192];
    private int _position, _available, _budget;
    private long _started;
    private CancellationToken _token;
    private bool _awaitingResponse, _receivedBytes;
    internal bool UseCrc { get; set; }
    internal bool Escaped { get; set; } = true;
    internal bool HasWritten { get; private set; }
    internal bool CanProbeConnect => _awaitingResponse && !_receivedBytes;
    internal CancellationToken Token => _token;
    internal SprdWire(ITransport transport, SprdProtocolOptions options)
    {
        _transport = transport; _options = options;
        int capacity = Math.Max(options.MaximumResponseBytes,
            Math.Max(options.Fdl2BlockSize, Math.Max(options.BootRomBlockSize, options.TransferBlockSize)) + 1) + 6;
        _body = ArrayPool<byte>.Shared.Rent(capacity);
        try { _encoded = ArrayPool<byte>.Shared.Rent(checked(capacity * 2 + 2)); }
        catch { ArrayPool<byte>.Shared.Return(_body, clearArray: true); throw; }
    }
    internal void Begin(CancellationToken token, int budget)
    { _token = token; _budget = budget; _started = Stopwatch.GetTimestamp(); HasWritten = false; }
    internal void Reset()
    { _position = _available = 0; _awaitingResponse = _receivedBytes = false; Escaped = true; UseCrc = true; }
    internal int Remaining
    {
        get
        {
            _token.ThrowIfCancellationRequested();
            long left = _budget - (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            if (left <= 0) throw new TimeoutException(Strings.Timeout);
            return checked((int)left);
        }
    }
    internal void Check() => _ = Remaining;
    internal void Delay(int milliseconds)
    {
        int wait = Math.Min(milliseconds, Remaining);
        if (_token.CanBeCanceled) _token.WaitHandle.WaitOne(wait);
        else if (wait > 0) Thread.Sleep(wait);
        Check();
        if (wait < milliseconds) throw new TimeoutException(Strings.Timeout);
    }
    internal SprdResponse Command(ushort command, ReadOnlySpan<byte> payload = default, bool detectChecksum = false,
        int? responseTimeoutMilliseconds = null)
    {
        _awaitingResponse = _receivedBytes = false;
        Check(); long commandStart = Stopwatch.GetTimestamp();
        int written;
        if (command == SprdCommand.CheckBaud) { _encoded[0] = 0x7e; written = 1; }
        else
        {
            int length = checked(payload.Length + (_options.PadOddPayloads ? payload.Length % 2 : 0));
            if (length > ushort.MaxValue || length + 6 > _body.Length) throw new ArgumentOutOfRangeException(nameof(payload));
            var body = _body.AsSpan(0, length + 6);
            BinaryPrimitives.WriteUInt16BigEndian(body, command);
            BinaryPrimitives.WriteUInt16BigEndian(body[2..], (ushort)length);
            payload.CopyTo(body[4..]);
            if (length != payload.Length) body[4 + payload.Length] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(body[(length + 4)..], Checksum(body[..(length + 4)], UseCrc));
            written = 0; _encoded[written++] = 0x7e;
            foreach (byte b in body)
            {
                if (Escaped && b is 0x7e or 0x7d) { _encoded[written++] = 0x7d; _encoded[written++] = (byte)(b ^ 0x20); }
                else _encoded[written++] = b;
            }
            _encoded[written++] = 0x7e;
        }
        Log.ForContext<SprdWire>().Debug(Strings.Command, command, payload.Length);
        HasWritten = true; // Even a partial Write failure invalidates this stream.
        _transport.Write(_encoded.AsSpan(0, written));
        _awaitingResponse = true;
        var response = ReceiveResponse(command, commandStart, detectChecksum, responseTimeoutMilliseconds);
        _awaitingResponse = false;
        return response;
    }
    internal void Raw(ReadOnlySpan<byte> payload)
    {
        Check(); long commandStart = Stopwatch.GetTimestamp();
        if (payload.IsEmpty || payload.Length > _options.RawDataFlushSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));
        HasWritten = true;
        _transport.Write(payload);
        if (_transport is IUsbTransport usb && payload.Length % _options.RawDataUsbPacketSize!.Value == 0)
        {
            Check(); usb.WriteZeroLengthPacket();
        }
        var response = ReceiveResponse(SprdCommand.Midst, commandStart);
        if (response.Type != SprdCommand.Ack) throw new SprdProtocolException(SprdCommand.Midst, response.Type);
    }
    private SprdResponse ReceiveResponse(ushort command, long commandStart, bool detectChecksum = false,
        int? responseTimeoutMilliseconds = null)
    {
        int commandTimeout = Math.Min(_options.CommandTimeoutMilliseconds, responseTimeoutMilliseconds ?? int.MaxValue);
        for (int logFrames = 0; ; logFrames++)
        {
            var response = Receive(command, commandStart, detectChecksum, commandTimeout);
            if (response.Type != SprdCommand.Log) return response;
            if (logFrames >= _options.MaximumLogFrames) throw new SprdProtocolException(command);
        }
    }
    internal SprdResponse Expect(ushort command, ReadOnlySpan<byte> payload = default, ushort expected = SprdCommand.Ack)
    {
        var response = Command(command, payload);
        if (response.Type != expected) throw new SprdProtocolException(command, response.Type);
        return response;
    }
    private byte Next(long commandStart, int commandTimeout)
    {
        if (_position == _available)
        {
            int timeout = ResponseBudget(commandStart, commandTimeout);
            _available = _transport.Read(_input, 0, _input.Length, timeout); _position = 0;
            if (_available <= 0 || _available > _input.Length) throw new TimeoutException(Strings.Timeout);
            ResponseBudget(commandStart, commandTimeout);
        }
        // Buffered decoding is CPU work, not a new I/O wait. Keep checks bounded without
        // two clock reads for every payload byte; each refill and frame completion also checks.
        else if ((_position & 255) == 0) ResponseBudget(commandStart, commandTimeout);
        _receivedBytes = true;
        return _input[_position++];
    }
    private int ResponseBudget(long commandStart, int commandTimeout)
    {
        int timeout = Math.Min(Remaining, commandTimeout - checked((int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(commandStart).TotalMilliseconds)));
        if (timeout <= 0) throw new TimeoutException(Strings.Timeout);
        return timeout;
    }
    private SprdResponse Receive(ushort command, long commandStart, bool detectChecksum, int commandTimeout)
    {
        int skipped = 0;
        while (Next(commandStart, commandTimeout) != 0x7e) if (++skipped > 64) throw new SprdProtocolException(command);
        int count = 0, expectedLength = -1, wireBytes = 0; bool escaping = false;
        while (true)
        {
            if (++wireBytes > checked((_options.MaximumResponseBytes + 6) * 2 + 66)) throw new SprdProtocolException(command);
            byte b = Next(commandStart, commandTimeout);
            if (Escaped)
            {
                if (escaping)
                {
                    if (b is not (0x5e or 0x5d)) throw new SprdProtocolException(command);
                    b ^= 0x20; escaping = false;
                }
                else if (b == 0x7d) { escaping = true; continue; }
                else if (b == 0x7e)
                {
                    if (count == 0) continue; // Consecutive opening flags, with the same command budget.
                    break;
                }
            }
            else if (count == 0 && b == 0x7e) continue;
            else if (!Escaped && count == expectedLength)
            {
                if (b != 0x7e) throw new SprdProtocolException(command);
                break;
            }
            if (count >= _options.MaximumResponseBytes + 6 || expectedLength >= 0 && count >= expectedLength)
                throw new SprdProtocolException(command);
            _body[count++] = b;
            if (count == 4)
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(_body.AsSpan(2));
                if (length > _options.MaximumResponseBytes) throw new SprdProtocolException(command);
                expectedLength = length + 6;
            }
        }
        if (count < 6 || count != expectedLength || escaping) throw new SprdProtocolException(command);
        var checkedBytes = _body.AsSpan(0, count - 2);
        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(_body.AsSpan(count - 2));
        bool useCrc = UseCrc;
        if (detectChecksum)
        {
            bool crcMatches = Checksum(checkedBytes, true) == checksum;
            bool fdlMatches = Checksum(checkedBytes, false) == checksum;
            if (crcMatches == fdlMatches) throw new InvalidOperationException(Strings.EntryDetectionFailed);
            useCrc = crcMatches;
        }
        else if (Checksum(checkedBytes, useCrc) != checksum) throw new SprdProtocolException(command);
        ResponseBudget(commandStart, commandTimeout);
        return new(BinaryPrimitives.ReadUInt16BigEndian(_body), _body.AsMemory(4, count - 6), useCrc);
    }
    private static ushort Checksum(ReadOnlySpan<byte> bytes, bool crc)
    {
        if (crc)
        {
            ushort value = 0;
            foreach (byte b in bytes)
            {
                value ^= (ushort)(b << 8);
                for (int bit = 0; bit < 8; bit++) value = (ushort)((value << 1) ^ ((value & 0x8000) == 0 ? 0 : 0x1021));
            }
            return value;
        }
        uint sum = 0; int index = 0;
        for (; index + 1 < bytes.Length; index += 2) sum += BinaryPrimitives.ReadUInt16LittleEndian(bytes[index..]);
        if (index < bytes.Length) sum += bytes[index];
        while (sum > ushort.MaxValue) sum = (sum & ushort.MaxValue) + (sum >> 16);
        return BinaryPrimitives.ReverseEndianness((ushort)~sum);
    }
    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_body, clearArray: true);
        ArrayPool<byte>.Shared.Return(_encoded, clearArray: true);
        Array.Clear(_input);
    }
}
