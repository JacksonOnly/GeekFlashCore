using System.Buffers.Binary;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal static class CodecLimits
{
    // Read the XZ index and each block's filter properties before LZMA2 allocates a dictionary.
    internal static void ValidateXz(Stream s, FirmwareOpenOptions options, CancellationToken ct)
    {
        if (s.Length < 24) throw new InvalidDataException(Strings.Truncated);
        Span<byte> footer = stackalloc byte[12]; s.Position = s.Length - 12; s.ReadExactly(footer);
        if (footer[10] != 'Y' || footer[11] != 'Z') throw new InvalidDataException(Strings.InvalidMetadata);
        long indexSize = checked(((long)BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) + 1) * 4), indexStart = s.Length - 12 - indexSize;
        if (indexSize > options.MaximumMetadataBytes || indexStart < 12) throw new InvalidDataException(Strings.InvalidMetadata);
        byte[] index = new byte[checked((int)indexSize)]; s.Position = indexStart; s.ReadExactly(index);
        var values = new XzIntegers(index); if (values.Byte() != 0) throw new InvalidDataException(Strings.InvalidMetadata);
        ulong count = values.Read(); if (count > (ulong)options.MaximumSegments) throw new InvalidDataException(Strings.InvalidMetadata);
        long block = 12; Span<byte> header = stackalloc byte[1024];
        for (ulong i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested(); long unpadded = ParseContext.Long(values.Read()); values.Read();
            SourceStream.Range(indexStart, block, unpadded); s.Position = block; int first = s.ReadByte(); if (first <= 0) throw new InvalidDataException(Strings.InvalidMetadata);
            int size = checked((first + 1) * 4); if (size > unpadded) throw new InvalidDataException(Strings.InvalidMetadata);
            header[0] = (byte)first; s.ReadExactly(header.Slice(1, size - 1)); var fields = new XzIntegers(header.Slice(1, size - 5)); byte flags = fields.Byte();
            if ((flags & 0x3c) != 0) throw new InvalidDataException(Strings.InvalidMetadata);
            if ((flags & 0x40) != 0) fields.Read(); if ((flags & 0x80) != 0) fields.Read();
            int filters = (flags & 3) + 1;
            for (int f = 0; f < filters; f++)
            {
                ulong id = fields.Read(), length = fields.Read(); if (length > 1024) throw new InvalidDataException(Strings.InvalidMetadata);
                var properties = fields.Take((int)length);
                if (id == 0x21)
                {
                    if (properties.Length != 1 || properties[0] > 40) throw new InvalidDataException(Strings.InvalidMetadata);
                    int p = properties[0]; long dictionary = p == 40 ? uint.MaxValue : checked((long)(2 | (p & 1)) << (p / 2 + 11));
                    if (dictionary > options.MaximumDecoderWindowBytes) throw new InvalidDataException(Strings.InvalidMetadata);
                }
            }
            block = checked(block + (unpadded + 3) / 4 * 4);
        }
        if (block != indexStart) throw new InvalidDataException(Strings.InvalidMetadata);
        ct.ThrowIfCancellationRequested();
    }
    private ref struct XzIntegers(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _bytes = bytes;
        internal byte Byte() { if (_bytes.IsEmpty) throw new InvalidDataException(Strings.Truncated); byte b = _bytes[0]; _bytes = _bytes[1..]; return b; }
        internal ReadOnlySpan<byte> Take(int n) { if (n > _bytes.Length) throw new InvalidDataException(Strings.Truncated); var b = _bytes[..n]; _bytes = _bytes[n..]; return b; }
        internal ulong Read()
        {
            ulong result = 0; for (int i = 0; i < 9; i++) { byte b = Byte(); result |= (ulong)(b & 127) << (7 * i); if ((b & 128) == 0) return result; }
            throw new InvalidDataException(Strings.InvalidMetadata);
        }
    }
}
