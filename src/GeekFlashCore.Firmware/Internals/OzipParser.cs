using System.Buffers.Text;
using System.Security.Cryptography;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal readonly record struct OzipBlock(long OutputOffset, long InputOffset, long Length, int Cycle);

internal static class OzipParser
{
    private static byte[] FindKey(ReadOnlySpan<byte> test)
    {
        using var aes = Aes.Create(); Span<byte> plain = stackalloc byte[16];
        foreach (var key in FirmwareKeys.Ozip)
        {
            aes.Key = key; aes.DecryptEcb(test, plain, PaddingMode.None);
            if (plain[..4].SequenceEqual("PK\x03\x04"u8) || plain[..4].SequenceEqual("AVB0"u8) || plain[..4].SequenceEqual("ANDR"u8)) return key.ToArray();
        }
        throw new NotSupportedException(Strings.Unsupported);
    }
    internal static void Payload(ParseContext c)
    {
        long length = c.Source.Length - 0x1050; byte[] key = c.Package.Secret(FindKey(c.Read(0x1050, 16)));
        OzipBlock[] blocks = [new(0, 0x1050, length, 0x4010)];
        c.Add("decrypted.zip", length, ct => new OzipStream(SourceStream.Open(c.Source), blocks, length, key, ct));
        c.Package.SetFormat(FirmwareFormat.Ozip);
    }
    internal static void ZipEntry(ParseContext c, string name, long storedLength, Func<CancellationToken, Stream> factory)
    {
        using var input = new ReplayStream(() => factory(c.Cancellation), storedLength, c.Options.BufferSize, c.Cancellation);
        var blocks = new List<OzipBlock>(); long pos = 0, total = 0; byte[]? key = null;
        try
        {
            byte[] h = new byte[0x50], test = new byte[16];
            while (pos < storedLength)
            {
                c.Limit(blocks.Count + 1, c.Options.MaximumSegments); c.Limit((blocks.Count + 1) * 32L, c.Options.MaximumMetadataBytes);
                SourceStream.Range(storedLength, pos, 0x50); input.Position = pos; input.ReadExactly(h);
                if (!h.AsSpan(0, 12).SequenceEqual("OPPOENCRYPT!"u8)) throw new InvalidDataException(Strings.InvalidMetadata);
                var text = h.AsSpan(0x10, 16); int nul = text.IndexOf((byte)0); if (nul >= 0) text = text[..nul];
                if (!Utf8Parser.TryParse(text, out long length, out int consumed) || consumed != text.Length || length is < 1 or > 0x40000) throw new InvalidDataException(Strings.InvalidMetadata);
                long body = pos + 0x50, physical = Math.Min(0x40000, storedLength - body);
                if (length > physical || physical < 16 || (length % 0x4000 is > 0 and < 16 && physical < length + 16 - length % 0x4000)) throw new InvalidDataException(Strings.InvalidRange);
                if (key is null) { input.ReadExactly(test); key = FindKey(test); }
                blocks.Add(new(total, body, length, 0x4000)); total = checked(total + length); pos = checked(body + physical);
            }
            if (key is null) throw new InvalidDataException(Strings.InvalidMetadata);
            c.Package.Secret(key); byte[] found = key; key = null; OzipBlock[] map = blocks.ToArray();
            c.Add(name, total, ct => new OzipStream(new ReplayStream(() => factory(ct), storedLength, c.Options.BufferSize, ct), map, total, found, ct));
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
}

internal sealed class OzipStream : ReadOnlyStream
{
    private readonly Stream _source;
    private readonly OzipBlock[] _blocks;
    private readonly long _length;
    private readonly Aes _aes;
    internal OzipStream(Stream source, OzipBlock[] blocks, long length, byte[] key, CancellationToken ct) : base(ct)
    {
        _source = source; _blocks = blocks; _length = length;
        try { _aes = Aes.Create(); _aes.Key = key; } catch { source.Dispose(); throw; }
    }
    public override long Length { get { Check(); return _length; } }
    public override int Read(Span<byte> buffer)
    {
        Check(); if (buffer.Length == 0 || Cursor == _length) return 0;
        int low = 0, high = _blocks.Length - 1;
        while (low < high) { int mid = low + (high - low + 1) / 2; if (_blocks[mid].OutputOffset <= Cursor) low = mid; else high = mid - 1; }
        var b = _blocks[low]; long relative = Cursor - b.OutputOffset; int cycle = (int)(relative % b.Cycle);
        int n = (int)Math.Min(buffer.Length, b.Length - relative);
        if (cycle < 16)
        {
            long start = b.InputOffset + relative - cycle; Span<byte> cipher = stackalloc byte[16], plain = stackalloc byte[16];
            if (_source.Length - start < 16)
            { _source.Position = start + cycle; n = Math.Min(n, 16 - cycle); _source.ReadExactly(buffer[..n]); }
            else
            { _source.Position = start; _source.ReadExactly(cipher); _aes.DecryptEcb(cipher, plain, PaddingMode.None); n = Math.Min(n, 16 - cycle); plain.Slice(cycle, n).CopyTo(buffer); }
        }
        else { n = Math.Min(n, b.Cycle - cycle); _source.Position = b.InputOffset + relative; _source.ReadExactly(buffer[..n]); }
        Cursor += n; return n;
    }
    protected override void Dispose(bool disposing)
    {
        try { if (disposing && CanRead) { try { _source.Dispose(); } finally { _aes.Dispose(); } } }
        finally { base.Dispose(disposing); }
    }
}
