using System.IO.Compression;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal static class FlatParsers
{
    internal static void Pac(ParseContext c)
    {
        byte[] header = c.Read(0, 2124); if (ParseContext.U32(header, 2116) != 0xFFFAFFFA) throw new InvalidDataException(Strings.InvalidMetadata);
        uint count = ParseContext.U32(header, 1076), offset = ParseContext.U32(header, 1080);
        c.Limit(count, c.Options.MaximumEntries); c.Limit((long)count * 2580, c.Options.MaximumMetadataBytes);
        long table = offset == 0 ? 2124 : offset; SourceStream.Range(c.Source.Length, table, (long)count * 2580);
        for (int i = 0; i < count; i++)
        {
            byte[] e = c.Read(checked(table + i * 2580L), 2580);
            long size = ParseContext.Long(((ulong)ParseContext.U32(e, 1532) << 32) | ParseContext.U32(e, 1540));
            long start = ParseContext.Long(((ulong)ParseContext.U32(e, 1536) << 32) | ParseContext.U32(e, 1552));
            string id = ParseContext.Unicode(e.AsSpan(4, 512));
            string name = ParseContext.Unicode(e.AsSpan(516, 512)); if (name.Length == 0) name = id;
            c.Slice(name, start, size, id.Length == 0 ? null : id);
        }
    }
    internal static void Kdz(ParseContext c)
    {
        ulong magic = ParseContext.U64(c.Read(0, 8), 0);
        if (magic is not (0x8025313400000528 or 0x5044793200000518 or 0x2522382400000528)) throw new InvalidDataException(Strings.InvalidMetadata);
        long pos = 8, firstData = c.Source.Length;
        while (pos < firstData)
        {
            c.Limit((c.Entries.Count + 1) * 272L, c.Options.MaximumMetadataBytes);
            byte[] e = c.Read(pos, 272); string name = ParseContext.Text(e.AsSpan(0, 256));
            long size = ParseContext.Long(ParseContext.U64(e, 256)), start = ParseContext.Long(ParseContext.U64(e, 264));
            if (name.Length == 0) break;
            if (start < pos + 272) throw new InvalidDataException(Strings.InvalidMetadata);
            firstData = Math.Min(firstData, start); c.Slice(name, start, size); pos += 272;
            if (pos >= firstData) break;
            c.Input.Position = pos; int marker = c.Input.ReadByte();
            if (marker is 0 or -1) break; if (marker == 3) pos++;
        }
        if (c.Entries.Count == 0) throw new InvalidDataException(Strings.InvalidMetadata);
    }
    internal static void Dz(ParseContext c)
    {
        byte[] h = c.Read(0, 512); if (ParseContext.U32(h, 0) != 0x74189632) throw new InvalidDataException(Strings.InvalidMetadata);
        uint count = ParseContext.U32(h, 192); c.Limit(count, c.Options.MaximumEntries); c.Limit(count * 512L, c.Options.MaximumMetadataBytes);
        long pos = 512;
        for (int i = 0; i < count; i++)
        {
            byte[] e = c.Read(pos, 512); if (ParseContext.U32(e, 0) != 0x78951230) throw new InvalidDataException(Strings.InvalidMetadata);
            string name = ParseContext.Text(e.AsSpan(36, 64)); long size = ParseContext.U32(e, 100), stored = ParseContext.U32(e, 104), start = pos + 512;
            SourceStream.Range(c.Source.Length, start, stored);
            c.Add(name, size, ct => new ReplayStream(() => new ZLibStream(SourceStream.Slice(c.Source, start, stored, ct), CompressionMode.Decompress), size, c.Options.BufferSize, ct));
            pos = checked(start + stored);
        }
    }
    internal static void UpdateApp(ParseContext c)
    {
        long pos = 92;
        while (pos < c.Source.Length)
        {
            byte[] e = c.Read(pos, 98); if (ParseContext.U32(e, 0) != 0xA55AAA55) throw new InvalidDataException(Strings.InvalidMetadata);
            uint headerSize = ParseContext.U32(e, 4); if (headerSize < 98) throw new InvalidDataException(Strings.InvalidMetadata);
            c.Limit(headerSize, c.Options.MaximumMetadataBytes);
            long start = checked(pos + headerSize), length = ParseContext.U32(e, 24);
            string name = ParseContext.Text(e.AsSpan(60, 16)); c.Slice(name + ".img", start, length);
            pos = checked((start + length + 3) / 4 * 4);
            if (pos > c.Source.Length && pos - c.Source.Length > 3) throw new InvalidDataException(Strings.InvalidMetadata);
        }
        if (c.Entries.Count == 0) throw new InvalidDataException(Strings.InvalidMetadata);
    }
}
