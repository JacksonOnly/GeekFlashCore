using System.Buffers.Binary;
using System.Buffers;
using System.Xml.Linq;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal static class OpsParser
{
    internal static bool IsOps(Stream s, FirmwareOpenOptions options)
    {
        if (s.Length < 512) return false; Span<byte> h = stackalloc byte[4]; s.Position = s.Length - 512 + 24; s.ReadExactly(h);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(h); if (size == 0 || size > Math.Min(options.MaximumMetadataBytes, 1572864)) return false;
        long stored = checked(size + 512 - size % 512), start = s.Length - 512 - stored; if (start < 0) return false;
        Span<byte> preview = stackalloc byte[16]; s.Position = start; s.ReadExactly(preview);
        Span<uint> state = stackalloc uint[4]; Span<byte> plain = stackalloc byte[16];
        foreach (var box in new[] { OpsCipher.MBox5, OpsCipher.MBox6, OpsCipher.MBox4 })
        {
            OpsCipher.BaseKey.CopyTo(state); OpsCipher.KeyUpdate(state, box);
            for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(plain[(i * 4)..], state[i] ^ BinaryPrimitives.ReadUInt32LittleEndian(preview[(i * 4)..]));
            if (plain.StartsWith("<?xml"u8) || plain.StartsWith("<Setting"u8)) return true;
        }
        return false;
    }
    internal static void Parse(ParseContext c)
    {
        long length = ParseContext.U32(c.Read(c.Source.Length - 512 + 24, 4), 0); c.Limit(length, c.Options.MaximumMetadataBytes);
        if (length == 0) throw new InvalidDataException(Strings.InvalidMetadata);
        long stored = checked(length + 512 - length % 512), start = c.Source.Length - 512 - stored;
        foreach (var box in new[] { OpsCipher.MBox5, OpsCipher.MBox6, OpsCipher.MBox4 })
        {
            c.Check(); using var decrypted = new OpsStream(SourceStream.Slice(c.Source, start, stored, c.Cancellation), stored, box, c.Cancellation);
            byte[] plain = new byte[checked((int)length)]; decrypted.ReadExactly(plain);
            if (!plain.AsSpan().StartsWith("<?xml"u8) && !plain.AsSpan().StartsWith("<Setting"u8)) continue;
            XElement root = FirmwareXml.Parse(c, plain); if (root.Name != "Setting") throw new InvalidDataException(Strings.InvalidMetadata);
            var known = new Dictionary<string, (long Start, long Size, bool Decrypt)>(StringComparer.Ordinal);
            void File(XElement e, bool encrypted)
            {
                string? name = (string?)e.Attribute("filename") ?? (string?)e.Attribute("Path"); if (string.IsNullOrWhiteSpace(name)) return;
                name = FirmwarePath.Normalize(name); long size = FirmwareXml.Number(e, "SizeInByteInSrc"), offset = FirmwareXml.Offset(e, 512);
                var layout = (offset, size, encrypted);
                if (known.TryGetValue(name, out var previous)) { if (previous != layout) throw new InvalidDataException(Strings.AmbiguousEntry); return; }
                known.Add(name, layout); SourceStream.Range(c.Source.Length, offset, size);
                if (encrypted)
                {
                    long physical = checked((size + 3) / 4 * 4); SourceStream.Range(c.Source.Length, offset, physical);
                    c.Add(name, size, ct => new OpsStream(SourceStream.Slice(c.Source, offset, physical, ct), size, box, ct));
                }
                else c.Slice(name, offset, size);
            }
            foreach (var e in FirmwareXml.Section(root, "SAHARA")?.Elements("File") ?? Enumerable.Empty<XElement>()) File(e, true);
            foreach (var e in FirmwareXml.Section(root, "UFS_PROVISION")?.Elements("File") ?? Enumerable.Empty<XElement>()) File(e, false);
            foreach (var group in root.Elements())
            {
                string name = group.Name.LocalName;
                if (name.StartsWith("Program", StringComparison.Ordinal) && int.TryParse(name.AsSpan(7), out int index) && index >= 0)
                { foreach (var e in group.Elements()) File(e, false); FirmwareXml.Script(c, $"rawprogram{index}.xml", group.Elements(), false); }
                else if (name.StartsWith("Patch", StringComparison.Ordinal) && int.TryParse(name.AsSpan(5), out int patch) && patch >= 0)
                    FirmwareXml.Script(c, $"patch{patch}.xml", group.Elements(), true);
            }
            return;
        }
        throw new NotSupportedException(Strings.Unsupported);
    }
}

internal sealed class OpsStream(Stream source, long length, byte[] box, CancellationToken ct) : ReadOnlyStream(ct)
{
    private readonly byte[] _cipher = ArrayPool<byte>.Shared.Rent(65536);
    private readonly byte[] _plain = ArrayPool<byte>.Shared.Rent(65536);
    private long _cacheOffset = -1;
    private int _cacheLength;
    public override long Length { get { Check(); return length; } }
    private void Fill(long offset)
    {
        _cacheOffset = -1; Span<uint> feedback = stackalloc uint[4]; Span<byte> previous = stackalloc byte[16];
        if (offset == 0) OpsCipher.BaseKey.CopyTo(feedback);
        else { source.Position = offset - 16; source.ReadExactly(previous); for (int i = 0; i < 4; i++) feedback[i] = BinaryPrimitives.ReadUInt32LittleEndian(previous[(i * 4)..]); }
        int count = (int)Math.Min(65536, source.Length - offset); source.Position = offset; source.ReadExactly(_cipher.AsSpan(0, count));
        for (int block = 0; block < count; block += 16)
        {
            Check(); int size = Math.Min(16, count - block);
            // Physical data is padded to uint32; only a final block shorter than 16 uses SBox.
            OpsCipher.KeyUpdate(feedback, size == 16 ? box : OpsCipher.SBox);
            for (int n = 0; n < size; n += 4)
            {
                uint cipher = BinaryPrimitives.ReadUInt32LittleEndian(_cipher.AsSpan(block + n));
                BinaryPrimitives.WriteUInt32LittleEndian(_plain.AsSpan(block + n), feedback[n / 4] ^ cipher); feedback[n / 4] = cipher;
            }
        }
        _cacheLength = count; _cacheOffset = offset;
    }
    public override int Read(Span<byte> buffer)
    {
        Check(); if (buffer.Length == 0 || Cursor == length) return 0; long offset = Cursor / 65536 * 65536;
        if (_cacheOffset != offset) Fill(offset); int inside = (int)(Cursor - offset), count = (int)Math.Min(buffer.Length, Math.Min(length - Cursor, _cacheLength - inside));
        _plain.AsSpan(inside, count).CopyTo(buffer); Cursor += count; return count;
    }
    protected override void Dispose(bool disposing)
    {
        try { if (disposing && CanRead) { try { source.Dispose(); } finally { ArrayPool<byte>.Shared.Return(_cipher, true); ArrayPool<byte>.Shared.Return(_plain, true); } } }
        finally { base.Dispose(disposing); }
    }
}
