using System.Buffers.Binary;
using SharpCompress.Archives.Zip;
using SharpCompress.Archives;
using SharpCompress.Readers;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal static class ZipParser
{
    internal static void Parse(ParseContext c)
    {
        if (c.Source.Length >= 12 && c.Read(0, 12).AsSpan().SequenceEqual("OPPOENCRYPT!"u8))
        { OzipParser.Payload(c); return; }
        Preflight(c);
        c.Input.Position = 0;
        using var archive = ZipArchive.OpenArchive(c.Input, new ReaderOptions { LeaveStreamOpen = true, BufferSize = c.Options.BufferSize });
        int index = 0;
        foreach (var e in archive.Entries)
        {
            c.Check(); int slot = index++;
            if (e.IsDirectory) { FirmwarePath.Normalize((e.Key ?? string.Empty).TrimEnd('/', '\\')); continue; }
            string name = FirmwarePath.Normalize(e.Key ?? throw new InvalidDataException(Strings.InvalidMetadata)); long length = e.Size;
            if (length < 0) throw new InvalidDataException(Strings.InvalidRange);
            Func<CancellationToken, Stream> decode = ct => OpenEntry(c, slot, ct);
            bool encrypted = false;
            if (length >= 0x60)
            {
                using var s = decode(c.Cancellation); byte[] h = new byte[0x60]; s.ReadExactly(h);
                if (h.AsSpan(0, 12).SequenceEqual("OPPOENCRYPT!"u8))
                { OzipParser.ZipEntry(c, name, length, decode); encrypted = true; c.Package.SetFormat(FirmwareFormat.Ozip); }
            }
            if (!encrypted) c.Add(name, length, ct => new ReplayStream(() => decode(ct), length, c.Options.BufferSize, ct));
        }
    }
    private static Stream OpenEntry(ParseContext c, int index, CancellationToken ct)
    {
        c.Check(); ct.ThrowIfCancellationRequested();
        Stream source = new DecoderInputStream(SourceStream.Open(c.Source), ct); IArchive? archive = null;
        try
        {
            archive = ZipArchive.OpenArchive(source, new ReaderOptions { LeaveStreamOpen = false, BufferSize = c.Options.BufferSize });
            var e = archive.Entries.ElementAt(index); return new OwnedStream(e.OpenEntryStream(), archive);
        }
        catch { archive?.Dispose(); source.Dispose(); throw; }
    }
    private static void Preflight(ParseContext c)
    {
        // Fixed signature-search scratch is separate from the serialized metadata budget.
        int size = (int)Math.Min(65557, c.Source.Length); byte[] tail = new byte[size];
        c.Check(); c.Input.Position = c.Source.Length - size; c.Input.ReadExactly(tail);
        int end = -1;
        for (int i = size - 22; i >= 0; i--)
            if (ParseContext.U32(tail, i) == 0x06054B50 && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == size) { end = i; break; }
        if (end < 0) throw new InvalidDataException(Strings.InvalidMetadata);
        var e = tail.AsSpan(end); if (ParseContext.U32(e, 4) != 0) throw new NotSupportedException(Strings.Unsupported);
        long count = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]), length = ParseContext.U32(e, 12), offset = ParseContext.U32(e, 16);
        if (count == 65535 || length == uint.MaxValue || offset == uint.MaxValue)
        {
            long locator = c.Source.Length - size + end - 20; byte[] l = c.Read(locator, 20);
            if (ParseContext.U32(l, 0) != 0x07064B50 || ParseContext.U32(l, 4) != 0 || ParseContext.U32(l, 16) != 1) throw new InvalidDataException(Strings.InvalidMetadata);
            long start = ParseContext.Long(ParseContext.U64(l, 8)); byte[] h = c.Read(start, 56);
            if (ParseContext.U32(h, 0) != 0x06064B50 || ParseContext.U32(h, 16) != 0 || ParseContext.U32(h, 20) != 0) throw new InvalidDataException(Strings.InvalidMetadata);
            count = ParseContext.Long(ParseContext.U64(h, 32)); length = ParseContext.Long(ParseContext.U64(h, 40)); offset = ParseContext.Long(ParseContext.U64(h, 48));
        }
        c.Limit(count, c.Options.MaximumEntries); c.Limit(length, c.Options.MaximumMetadataBytes); SourceStream.Range(c.Source.Length, offset, length);
        // Count the central headers too: do not trust only the EOCD's count before the archive allocates its catalog.
        long cursor = offset, limit = checked(offset + length), actual = 0;
        while (cursor < limit)
        {
            byte[] h = c.Read(cursor, 46); if (ParseContext.U32(h, 0) != 0x02014B50) throw new InvalidDataException(Strings.InvalidMetadata);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8));
            if ((flags & 0x41) != 0) throw new NotSupportedException(Strings.Unsupported);
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(10));
            if (method is not (0 or 8 or 12)) throw new NotSupportedException(Strings.Unsupported);
            c.Limit(++actual, c.Options.MaximumEntries);
            cursor = checked(cursor + 46 + BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(28)) + BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(30)) + BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(32)));
        }
        if (cursor != limit || actual != count) throw new InvalidDataException(Strings.InvalidMetadata);
    }
}
