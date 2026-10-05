using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Internals;

internal sealed class MtkWire(IUsbTransport transport, MtkProtocolOptions options)
{
    public CancellationToken Token
    {
        get; private set;
    }
    private long _deadline;
    public bool HasWritten
    {
        get; private set;
    }
    public MtkBootStage Stage
    {
        get; set;
    }
    public uint Command
    {
        get; set;
    }
    public int WritePacketLength { get; set; } = options.BufferSize;
    public void Begin(CancellationToken token, int timeout)
    {
        Token = token;
        _deadline = checked(Environment.TickCount64 + timeout);
        HasWritten = false;
    }
    public void Check()
    {
        Token.ThrowIfCancellationRequested();
        if (Environment.TickCount64 >= _deadline)
            throw new TimeoutException(Strings.Timeout);
    }
    public int RemainingTimeoutMilliseconds
    {
        get
        {
            Check();
            return checked((int)Math.Min(int.MaxValue, Math.Max(1, _deadline - Environment.TickCount64)));
        }
    }
    public void Write(ReadOnlySpan<byte> data)
    {
        Check();
        HasWritten = true;
        transport.Write(data);
    }
    public void ConfigureCdc(int controlInterface)
    {
        Check();
        HasWritten = true;
        ReadOnlySpan<byte> coding = [0, 0xc2, 1, 0, 0, 0, 8];
        transport.ControlOut(0x21, 0x20, 0, checked((ushort)controlInterface), coding);
        Check();
        transport.ControlOut(0x21, 0x22, 3, checked((ushort)controlInterface), []);
    }
    public void ZeroLengthPacket()
    {
        Check();
        HasWritten = true;
        transport.WriteZeroLengthPacket();
    }
    public void Read(Span<byte> data)
    {
        int done = 0;
        while (done < data.Length)
        {
            Check();
            int timeout = checked((int)Math.Min(options.ReadTimeoutMilliseconds, Math.Max(1, _deadline - Environment.TickCount64)));
            int read = transport.Read(data[done..], timeout);
            if (read <= 0 || read > data.Length - done)
                throw new EndOfStreamException(Strings.FormatInvalidData("USB read"));
            done += read;
        }
    }
    public byte ReadByte()
    {
        Span<byte> b = stackalloc byte[1];
        Read(b);
        return b[0];
    }
    public ushort Read16()
    {
        Span<byte> b = stackalloc byte[2];
        Read(b);
        return BinaryPrimitives.ReadUInt16BigEndian(b);
    }
    public uint Read32()
    {
        Span<byte> b = stackalloc byte[4];
        Read(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }
    public void WriteByte(byte value)
    {
        Span<byte> b = stackalloc byte[1];
        b[0] = value;
        Write(b);
    }
    public void Echo(ReadOnlySpan<byte> data)
    {
        if (data.Length > 16)
            throw new ArgumentOutOfRangeException(nameof(data));
        Span<byte> echo = stackalloc byte[data.Length];
        Write(data);
        Read(echo);
        if (!data.SequenceEqual(echo))
            throw Failure();
    }
    public void EchoByte(byte value)
    {
        Command = value;
        Span<byte> b = stackalloc byte[1];
        b[0] = value;
        Echo(b);
    }
    public void Echo32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        Echo(b);
    }
    public void Status16()
    {
        ushort status = Read16();
        if (status != 0)
            throw Failure(status);
    }
    public MtkProtocolException Failure(uint status = 0) => new(Stage, Command, status);
    public void SendFrame(ReadOnlySpan<byte> data)
    {
        SendFrameHeader(data.Length);
        for (int offset = 0; offset < data.Length;)
        {
            int count = Math.Min(WritePacketLength, data.Length - offset);
            Write(data.Slice(offset, count));
            offset += count;
        }
    }
    public void SendFrameHeader(long length)
    {
        if (length <= 0 || length > uint.MaxValue)
            throw new MtkResourceException("frame length");
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xfeeeeeef);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)length);
        Write(header);
    }
    public int ReadFrame(Span<byte> destination)
    {
        Span<byte> header = stackalloc byte[12];
        byte[]? message = null;
        try
        {
            for (int i = 0; i <= options.MaximumMessages; i++)
            {
                Read(header);
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header), type = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]),
                    length = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
                if (magic != 0xfeeeeeef || type is not (1 or 2) || length == 0 || length > options.MaximumFrameSize)
                    throw Failure();
                if (type == 1)
                {
                    if (length > destination.Length)
                        throw Failure();
                    Read(destination[..(int)length]);
                    return (int)length;
                }
                if (i == options.MaximumMessages)
                    throw Failure();
                message ??= ArrayPool<byte>.Shared.Rent(options.BufferSize);
                for (uint left = length; left > 0;)
                {
                    int n = (int)Math.Min((uint)message.Length, left);
                    Read(message.AsSpan(0, n));
                    left -= (uint)n;
                }
                // Sensitive device text is drained, never logged.
            }
            throw Failure();
        }
        finally { if (message is not null) ArrayPool<byte>.Shared.Return(message, true); }
    }
    public byte[] ReadSmallFrame(int maximum)
    {
        byte[] b = ArrayPool<byte>.Shared.Rent(maximum);
        try
        {
            int n = ReadFrame(b.AsSpan(0, maximum));
            return b.AsSpan(0, n).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(b, true); }
    }
    public uint ReadStatus(params uint[] accepted)
    {
        Span<byte> b = stackalloc byte[4];
        if (ReadFrame(b) != 4)
            throw Failure();
        uint status = BinaryPrimitives.ReadUInt32LittleEndian(b);
        if (accepted.Length == 0 ? status != 0 : !accepted.Contains(status))
            throw Failure(status);
        return status;
    }
    public static byte[] Le32(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }
    public static ushort Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        foreach (byte b in data)
            sum += b;
        return (ushort)(sum & 0xffff);
    }
}
