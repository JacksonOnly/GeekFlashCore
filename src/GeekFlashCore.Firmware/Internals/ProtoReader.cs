using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

// Only the stable payload fields are read; unknown scalar/length fields are skipped without allocation.
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _remaining = data;
    internal bool Next(out ProtoField field)
    {
        field = default; if (_remaining.IsEmpty) return false;
        ulong tag = Varint(); int number = checked((int)(tag >> 3)), wire = (int)(tag & 7);
        if (number == 0 || number > 0x1fffffff) throw new InvalidDataException(Strings.InvalidMetadata);
        switch (wire)
        {
            case 0: field = new(number, wire, Varint(), default); break;
            case 1: Take(8); field = new(number, wire, 0, default); break;
            case 2:
                ulong n = Varint(); if (n > (ulong)_remaining.Length) throw new InvalidDataException(Strings.InvalidMetadata);
                field = new(number, wire, 0, Take((int)n)); break;
            case 5: Take(4); field = new(number, wire, 0, default); break;
            default: throw new InvalidDataException(Strings.InvalidMetadata);
        }
        return true;
    }
    private ReadOnlySpan<byte> Take(int size)
    { if (size > _remaining.Length) throw new InvalidDataException(Strings.InvalidMetadata); var part = _remaining[..size]; _remaining = _remaining[size..]; return part; }
    private ulong Varint()
    {
        ulong value = 0;
        for (int i = 0; i < 10; i++)
        {
            if (_remaining.IsEmpty) throw new InvalidDataException(Strings.InvalidMetadata);
            byte b = _remaining[0]; _remaining = _remaining[1..]; if (i == 9 && b > 1) throw new InvalidDataException(Strings.InvalidMetadata);
            value |= (ulong)(b & 127) << (i * 7); if ((b & 128) == 0) return value;
        }
        throw new InvalidDataException(Strings.InvalidMetadata);
    }
}
internal readonly ref struct ProtoField(int number, int wire, ulong scalar, ReadOnlySpan<byte> bytes)
{
    internal int Number { get; } = number;
    internal int Wire { get; } = wire;
    internal ulong Scalar { get; } = scalar;
    internal ReadOnlySpan<byte> Bytes { get; } = bytes;
    internal void Require(int wire) { if (Wire != wire) throw new InvalidDataException(Strings.InvalidMetadata); }
}
