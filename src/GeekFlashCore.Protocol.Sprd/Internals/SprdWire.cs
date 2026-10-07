using System.Diagnostics;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Sprd.Internals;

internal readonly record struct SprdResponse(ushort Type, ReadOnlyMemory<byte> Data);

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
    internal bool UseCrc { get; set; }
    internal bool Escaped { get; set; } = true;
    internal bool HasWritten { get; private set; }
    internal CancellationToken Token => _token;
    internal SprdWire(ITransport transport, SprdProtocolOptions options)
    {
        _transport = transport; _options = options;
        int capacity = Math.Max(options.MaximumResponseBytes, Math.Max(options.BootRomBlockSize, options.TransferBlockSize) + 1) + 6;
        _body = ArrayPool<byte>.Shared.Rent(capacity);
        try { _encoded = ArrayPool<byte>.Shared.Rent(checked(capacity * 2 + 2)); }
        catch { ArrayPool<byte>.Shared.Return(_body, clearArray: true); throw; }
    }
    internal void Begin(CancellationToken token, int budget)
    { _token = token; _budget = budget; _started = Stopwatch.GetTimestamp(); HasWritten = false; }
    internal void Reset() { _position = _available = 0; Escaped = true; UseCrc = true; }
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
    internal SprdResponse Command(ushort command, ReadOnlySpan<byte> payload = default)
    {
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
        for (int logFrames = 0; ; logFrames++)
        {
            var response = Receive(command, commandStart);
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
    private byte Next(long commandStart)
    {
        int timeout = Math.Min(Remaining, _options.CommandTimeoutMilliseconds - checked((int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(commandStart).TotalMilliseconds)));
        if (timeout <= 0) throw new TimeoutException(Strings.Timeout);
        if (_position == _available)
        {
            _available = _transport.Read(_input, 0, _input.Length, timeout); _position = 0;
            if (_available <= 0 || _available > _input.Length) throw new TimeoutException(Strings.Timeout);
            Check();
        }
        return _input[_position++];
    }
    private SprdResponse Receive(ushort command, long commandStart)
    {
        int skipped = 0;
        while (Next(commandStart) != 0x7e) if (++skipped > 64) throw new SprdProtocolException(command);
        int count = 0, expectedLength = -1, wireBytes = 0; bool escaping = false;
        while (true)
        {
            if (++wireBytes > checked((_options.MaximumResponseBytes + 6) * 2 + 66)) throw new SprdProtocolException(command);
            byte b = Next(commandStart);
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
        if (Checksum(_body.AsSpan(0, count - 2), UseCrc) != BinaryPrimitives.ReadUInt16BigEndian(_body.AsSpan(count - 2)))
            throw new SprdProtocolException(command);
        return new(BinaryPrimitives.ReadUInt16BigEndian(_body), _body.AsMemory(4, count - 6));
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
