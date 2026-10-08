namespace GeekFlashCore.Protocol.Mtk.Internals;

// Stack-owned startup packet cache. It cannot escape Probe or retain bytes across sessions.
internal ref struct MtkHandshakeReader
{
    private readonly MtkWire _wire;
    private readonly Span<byte> _packet;
    private readonly long _deadline;
    private int _position, _length;

    public MtkHandshakeReader(MtkWire wire, Span<byte> packet, int timeout)
    {
        _wire = wire;
        _packet = packet;
        _deadline = checked(Environment.TickCount64 + timeout);
        _position = _length = 0;
    }

    public void Check()
    {
        _wire.Check();
        if (Environment.TickCount64 >= _deadline)
            throw new TimeoutException(Strings.Timeout);
    }

    public byte ReadByte()
    {
        Check();
        if (_position < _length)
            return _packet[_position++];
        int timeout = checked((int)Math.Max(0, _deadline - Environment.TickCount64));
        if (_packet.IsEmpty)
        {
            // Preserve BROM's initial single-byte IN recovery and original USB receive shape.
            Span<byte> single = stackalloc byte[1];
            _wire.ReadStartupPacket(single, timeout);
            return single[0];
        }
        _length = _wire.ReadStartupPacket(_packet, timeout);
        _position = 1;
        return _packet[0];
    }

    public ushort Read16()
    {
        if (!_packet.IsEmpty)
            return (ushort)((ReadByte() << 8) | ReadByte());
        // Do not turn BROM's two-byte halfword transfers into one-byte USB requests.
        Check();
        Span<byte> word = stackalloc byte[2];
        _wire.Read(word, checked((int)Math.Max(0, _deadline - Environment.TickCount64)));
        Check();
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(word);
    }

    public void RequireEmpty()
    {
        Check();
        if (_position != _length)
            throw _wire.Failure();
    }
}
