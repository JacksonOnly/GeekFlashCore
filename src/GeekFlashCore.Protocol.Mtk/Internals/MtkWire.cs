using GeekFlashCore.Transport.Abstractions;
using Serilog;
using Serilog.Events;

namespace GeekFlashCore.Protocol.Mtk.Internals;

internal sealed class MtkWire(IUsbTransport transport, MtkProtocolOptions options, ILogger? logger = null)
{
    private const int MaximumConsecutiveDaZeroLengthPackets = 4;
    public ILogger Logger { get; } = (logger ?? Log.Logger).ForContext<MtkWire>();
    public bool IsPreloaderCandidate => transport.Identity.VendorId == 0x0e8d &&
        transport.Identity.ProductId is 0x2000 or 0x6000;
    public string CommandName { get; private set; } = "Handshake";
    public void TraceCommand(uint command, string name)
    {
        Command = command;
        CommandName = name;
        Logger.Debug(Strings.WireCommand, Stage, name, command);
    }
    public void TraceStatus(uint status, bool accepted)
    {
        if (Logger.IsEnabled(LogEventLevel.Debug))
            Logger.Debug(Strings.WireStatus, Stage, Command, status, accepted);
    }
    public void TraceStorage(MtkTransferKind operation, MtkStorageRegion region, long offset, long length)
    {
        if (Logger.IsEnabled(LogEventLevel.Debug))
            Logger.Debug(Strings.WireStorageRange, Stage, operation, region.Kind, region.WireId, offset, length);
    }
    public CancellationToken Token
    {
        get; private set;
    }
    private long _deadline;
    public bool HasWritten
    {
        get; private set;
    }
    private bool _hasRead;
    private byte? _startupByte;
    public MtkEntrySignal InspectEntrySignal()
    {
        Check();
        if (_startupByte is null)
        {
            Span<byte> one = stackalloc byte[1];
            _hasRead = true;
            int count = transport.ReadAvailable(one);
            Check();
            if (count == 0) return MtkEntrySignal.None;
            if (count != 1) throw Failure();
            _startupByte = one[0];
        }
        return _startupByte.Value switch { 0xc0 => MtkEntrySignal.Da1Sync, 0xef => MtkEntrySignal.FramedDa, _ => MtkEntrySignal.Other };
    }
    public void ClearStartup() => _startupByte = null;
    public bool HasIoAttempted => HasWritten || _hasRead;
    public MtkBootStage Stage
    {
        get; set;
    }
    public uint Command
    {
        get; set;
    }
    public int WritePacketLength { get; set; } = options.BufferSize;
    public int ReadPacketLength { get; set; } = options.MaximumFrameSize;
    public Action<int>? ProgressPercent { get; set; }
    public void Begin(CancellationToken token, int timeout)
    {
        Token = token;
        _deadline = checked(Environment.TickCount64 + timeout);
        HasWritten = false;
        _hasRead = false;
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
        long started = Environment.TickCount64;
        try
        {
            transport.Write(data);
            Check();
            if (Logger.IsEnabled(LogEventLevel.Debug))
                Logger.Debug(Strings.WireWrite, Stage, Command, data.Length, Environment.TickCount64 - started);
        }
        catch (Exception ex)
        {
            Logger.Debug(Strings.WireInterrupted, "Write", Stage, Command, data.Length, 0, 0,
                Environment.TickCount64 - started, Math.Max(0, _deadline - started), ex.GetType().Name);
            throw;
        }
    }
    public void MarkHostTransfer()
    {
        Check();
        HasWritten = true;
    }
    public void ConfigureCdc(int controlInterface)
    {
        Check();
        HasWritten = true;
        Logger.Debug(Strings.CdcSetup, controlInterface, 115200, 3);
        ReadOnlySpan<byte> coding = [0, 0xc2, 1, 0, 0, 0, 8];
        transport.ControlOut(0x21, 0x20, 0, checked((ushort)controlInterface), coding);
        Check();
        transport.ControlOut(0x21, 0x22, 3, checked((ushort)controlInterface), []);
        Check();
    }
    public void ConfigureIoTCdc()
    {
        if(transport.ControlInterfaceNumber is not { } number)return;
        Check();HasWritten=true;
        ReadOnlySpan<byte> coding=[0,0x10,0x0e,0,0,0,8]; // 921600 baud, 8N1.
        transport.ControlOut(0x21,0x20,0,checked((ushort)number),coding);Check();
    }
    public void ZeroLengthPacket()
    {
        Check();
        HasWritten = true;
        transport.WriteZeroLengthPacket();
        Check();
        Logger.Debug(Strings.WireZlp, Stage, Command);
    }
    public void Read(Span<byte> data, int? maximumTimeoutMilliseconds = null)
    {
        // Fragments share one logical read budget; slow trickles cannot extend it indefinitely.
        long started = Environment.TickCount64;
        int readTimeout = Math.Min(options.ReadTimeoutMilliseconds, maximumTimeoutMilliseconds ?? int.MaxValue);
        long deadline = Math.Min(_deadline, checked(started + readTimeout));
        int budget = (int)Math.Clamp(deadline - started, 0, int.MaxValue);
        int done = 0, fragments = 0, zeroLengthPackets = 0;
        if (!data.IsEmpty && _startupByte is { } pending)
        { data[done++] = pending; _startupByte = null; _hasRead = true; }
        try
        {
            while (done < data.Length)
            {
                Check();
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    throw new TimeoutException(Strings.Timeout);
                int timeout = checked((int)remaining);
                _hasRead = true;
                fragments++;
                int read = transport.Read(data[done..], timeout);
                Check();
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException(Strings.Timeout);
                if (read == 0 && (Stage is MtkBootStage.Da1 or MtkBootStage.Da2) && transport.IsOpen &&
                    zeroLengthPackets < MaximumConsecutiveDaZeroLengthPackets)
                {
                    // Successful native USB ZLP is not EOF. Continue the same read, never a
                    // command retry, under the original deadline and a finite consecutive cap.
                    zeroLengthPackets++;
                    Logger.Debug(Strings.DaZeroLengthIn, Stage, Command, zeroLengthPackets,
                        Math.Max(0, deadline - Environment.TickCount64));
                    continue;
                }
                if (read <= 0 || read > data.Length - done)
                    throw new EndOfStreamException(Strings.FormatInvalidData("USB read"));
                zeroLengthPackets = 0;
                done += read;
            }
            if (Logger.IsEnabled(LogEventLevel.Debug))
                Logger.Debug(Strings.WireRead, Stage, Command, data.Length, fragments, Environment.TickCount64 - started, budget);
        }
        catch (Exception ex)
        {
            Logger.Debug(Strings.WireInterrupted, "Read", Stage, Command, data.Length, done, fragments,
                Environment.TickCount64 - started, budget, ex.GetType().Name);
            throw;
        }
    }
    public int ReadStartupPacket(Span<byte> data, int maximumTimeout)
    {
        // One native packet, not an exact fill: READY and its response may share an IN transfer.
        Check();
        if (!data.IsEmpty && _startupByte is { } pending)
        { data[0] = pending; _startupByte = null; _hasRead = true; return 1; }
        int timeout = Math.Min(maximumTimeout, Math.Min(options.ReadTimeoutMilliseconds, RemainingTimeoutMilliseconds));
        if (timeout <= 0)
            throw new TimeoutException(Strings.Timeout);
        long started = Environment.TickCount64;
        int read = 0;
        _hasRead = true;
        try
        {
            read = transport.Read(data, timeout);
            Check();
            if (Environment.TickCount64 - started >= timeout)
                throw new TimeoutException(Strings.Timeout);
            if (read <= 0 || read > data.Length)
                throw new EndOfStreamException(Strings.FormatInvalidData("USB startup read"));
            Logger.Debug(Strings.StartupPacketRead, data.Length, read, Environment.TickCount64 - started, timeout);
            return read;
        }
        catch (Exception ex)
        {
            Logger.Debug(Strings.WireInterrupted, "Read", Stage, Command, data.Length, read, 1,
                Environment.TickCount64 - started, timeout, ex.GetType().Name);
            throw;
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
        TraceStatus(status, status == 0);
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
    public void SendUInt32Frame(uint value)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        SendFrame(data);
    }
    public void SendFrameHeader(long length)
    {
        // Zero-length parameter frames are required by the explicit V2 key-derive label/salt ABI.
        if (length < 0 || length > uint.MaxValue)
            throw new MtkResourceException("frame length");
        Span<byte> header = stackalloc byte[MtkDaFrame.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, MtkDaFrame.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)MtkDaFrameType.Flow);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)length);
        if (Logger.IsEnabled(LogEventLevel.Debug))
            Logger.Debug(Strings.WireFrame, "Write", Stage, Command, MtkDaFrameType.Flow, length);
        Write(header);
    }
    public int ReadFrame(Span<byte> destination)
    {
        int length = ReadFlowHeader(Math.Min(options.MaximumFrameSize, destination.Length));
        Read(destination[..length]);
        return length;
    }
    public int ReadStreamFrame(Stream output, int maximumLength, Span<byte> buffer)
        => ReadStreamFrame(output, maximumLength, buffer, options.MaximumXFlashDataFrameSize);

    public int ReadXmlStreamFrame(Stream output, int expectedLength, Span<byte> buffer)
        => ReadStreamFrame(output, Math.Min(expectedLength, options.MaximumXmlDataFrameSize), buffer,
            options.MaximumXmlDataFrameSize, expectedLength);

    private int ReadStreamFrame(Stream output, int maximumLength, Span<byte> buffer, int maximumFrameSize, int? expectedLength = null)
    {
        if (buffer.IsEmpty || maximumLength <= 0 || maximumLength > maximumFrameSize)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        int length = ReadFlowHeader(maximumLength);
        // XML file packets have an exact negotiated boundary. Validate before writing any payload.
        if (expectedLength is { } exact && length != exact)
            throw Failure();
        // All payload windows share a logical read deadline. A slow trickle cannot refresh it.
        long deadline = checked(Environment.TickCount64 + options.ReadTimeoutMilliseconds);
        int buffered = 0;
        for (int remaining = length; remaining > 0;)
        {
            Check();
            int count = Math.Min(remaining, Math.Min(options.BufferSize, buffer.Length - buffered));
            int received = ReadStreamWindow(buffer.Slice(buffered, count), deadline);
            buffered += received;
            remaining -= received;
            if (buffered == buffer.Length || remaining == 0)
            {
                output.Write(buffer[..buffered]);
                buffered = 0;
            }
        }
        Check();
        if (Environment.TickCount64 >= deadline) throw new TimeoutException(Strings.Timeout);
        return length;
    }
    private int ReadStreamWindow(Span<byte> buffer, long deadline)
    {
        long started = Environment.TickCount64;
        deadline = Math.Min(deadline, _deadline);
        int budget = (int)Math.Clamp(deadline - started, 0, int.MaxValue), fragments = 0;
        try
        {
            for (int zeros = 0; ; zeros++)
            {
                Check();
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) throw new TimeoutException(Strings.Timeout);
                _hasRead = true;
                fragments++;
                int received = transport.Read(buffer, checked((int)Math.Min(int.MaxValue, remaining)));
                Check();
                if (Environment.TickCount64 >= deadline) throw new TimeoutException(Strings.Timeout);
                if (received == 0 && transport.IsOpen && zeros < MaximumConsecutiveDaZeroLengthPackets) continue;
                if (received <= 0 || received > buffer.Length) throw new EndOfStreamException(Strings.FormatInvalidData("USB read"));
                if (Logger.IsEnabled(LogEventLevel.Debug))
                    Logger.Debug(Strings.WireRead, Stage, Command, received, fragments, Environment.TickCount64 - started, budget);
                return received;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(Strings.WireInterrupted, "Read", Stage, Command, buffer.Length, 0, fragments,
                Environment.TickCount64 - started, budget, ex.GetType().Name);
            throw;
        }
    }
    private int ReadFlowHeader(int maximumFlowLength)
    {
        Span<byte> header = stackalloc byte[MtkDaFrame.HeaderSize];
        byte[]? message = null;
        try
        {
            for (int i = 0; i <= options.MaximumMessages; i++)
            {
                Read(header);
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header), type = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]),
                    length = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
                if (Logger.IsEnabled(LogEventLevel.Debug))
                    Logger.Debug(Strings.WireFrame, "Read", Stage, Command, type, length);
                if (magic != MtkDaFrame.Magic || type is not ((uint)MtkDaFrameType.Flow or (uint)MtkDaFrameType.Message) || length == 0)
                    throw Failure();
                if (type == (uint)MtkDaFrameType.Flow)
                {
                    if (length > maximumFlowLength)
                        throw Failure();
                    return (int)length;
                }
                if (length > options.MaximumFrameSize)
                    throw Failure();
                if (i == options.MaximumMessages)
                    throw Failure();
                Logger.Debug(Strings.WireDeviceMessage, Stage, Command, length, i + 1);
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
        bool valid = accepted.Length == 0 ? status == 0 : accepted.Contains(status);
        TraceStatus(status, valid);
        if (!valid)
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
