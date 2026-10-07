using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal static class OfpParser
{
    internal static (byte[] Key, byte[] Iv) Derive(byte[][] table)
    {
        if (table.Length == 2) return (table[0].ToArray(), table[1].ToArray());
        byte[] Value(byte[] data)
        {
            Span<byte> decoded = stackalloc byte[16];
            for (int i = 0; i < 16; i++) { byte b = (byte)(data[i] ^ table[0][i]); decoded[i] = (byte)((b >> 4) | (b << 4)); }
            byte[] result = Encoding.ASCII.GetBytes(Convert.ToHexString(MD5.HashData(decoded)).ToLowerInvariant()[..16]);
            CryptographicOperations.ZeroMemory(decoded); return result;
        }
        return (Value(table[1]), Value(table[2]));
    }
    internal static byte[] Decrypt(byte[] cipher, byte[] key, byte[] iv)
    {
        using var stream = new CfbStream(new MemoryStream(cipher, false), cipher.Length, key, iv, default);
        byte[] output = new byte[cipher.Length]; stream.ReadExactly(output); return output;
    }
    private static (byte[] Key, byte[] Iv)? FindMtk(Stream s, CancellationToken ct = default)
    {
        if (s.Length < 108) return null;
        byte[] test = new byte[16]; s.Position = 0; s.ReadExactly(test);
        foreach (var table in FirmwareKeys.MediaTek)
        {
            ct.ThrowIfCancellationRequested(); var pair = Derive(table);
            byte[] plain = Decrypt(test, pair.Key, pair.Iv);
            bool matches = plain.AsSpan(0, 3).SequenceEqual("MMM"u8); CryptographicOperations.ZeroMemory(plain);
            if (matches) return pair; CryptographicOperations.ZeroMemory(pair.Key); CryptographicOperations.ZeroMemory(pair.Iv);
        }
        return null;
    }
    internal static bool IsMediaTek(Stream s)
    {
        var pair = FindMtk(s); if (pair is null) return false;
        CryptographicOperations.ZeroMemory(pair.Value.Key); CryptographicOperations.ZeroMemory(pair.Value.Iv); return true;
    }
    private static void Shuffle(Span<byte> bytes)
    { ReadOnlySpan<byte> mask = "geyixue"u8; for (int i = 0; i < bytes.Length; i++) { byte b = bytes[i]; bytes[i] = (byte)(((b >> 4) | (b << 4)) ^ mask[i % mask.Length]); } }
    internal static void MediaTek(ParseContext c)
    {
        var pair = FindMtk(c.Input, c.Cancellation) ?? throw new NotSupportedException(Strings.Unsupported);
        byte[] key = c.Package.Secret(pair.Key), iv = c.Package.Secret(pair.Iv);
        byte[] h = c.Read(c.Source.Length - 108, 108); Shuffle(h);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(72)); c.Limit(count, c.Options.MaximumEntries); c.Limit(count * 96L, c.Options.MaximumMetadataBytes);
        byte[] entries = c.Read(c.Source.Length - 108 - count * 96L, count * 96); Shuffle(entries);
        for (int i = 0; i < count; i++)
        {
            c.Check(); var e = entries.AsSpan(i * 96, 96); string name = ParseContext.Text(e.Slice(56, 32));
            if (name.Length == 0) name = ParseContext.Text(e[..32]);
            long start = ParseContext.Long(ParseContext.U64(e, 32)), length = ParseContext.Long(ParseContext.U64(e, 40)), encrypted = ParseContext.Long(ParseContext.U64(e, 48));
            c.Encrypted(name, start, length, encrypted, key, iv);
        }
    }
    internal static void Qualcomm(ParseContext c)
    {
        int page = 0;
        foreach (int size in new[] { 4096, 512 })
            if (c.Source.Length >= size && ParseContext.U32(c.Read(c.Source.Length - size + 16, 4), 0) == 0x7CEF) { page = size; break; }
        if (page == 0) throw new InvalidDataException(Strings.InvalidMetadata);
        long footer = c.Source.Length - page;
        byte[] meta = c.Read(footer + 20, 8); long offset = checked(ParseContext.U32(meta, 0) * (long)page), length = ParseContext.U32(meta, 4);
        if (length < 200) length = checked(footer - offset - 0x57); c.Limit(length, c.Options.MaximumMetadataBytes);
        byte[] cipher = c.Read(offset, checked((int)length));
        foreach (var table in FirmwareKeys.Qualcomm)
        {
            c.Check(); var pair = Derive(table); bool keep = false;
            try
            {
                byte[] test = Decrypt(cipher[..Math.Min(64, cipher.Length)], pair.Key, pair.Iv);
                bool matches = test.AsSpan().StartsWith("<?xml"u8) || test.AsSpan().StartsWith("<ProFile"u8);
                CryptographicOperations.ZeroMemory(test); if (!matches) continue;
                byte[] plain = Decrypt(cipher, pair.Key, pair.Iv);
                try
                {
                    var xml = FirmwareXml.Parse(c, plain);
                    if (xml.Name != "ProFile") throw new InvalidDataException(Strings.InvalidMetadata);
                    c.Package.Secret(pair.Key); c.Package.Secret(pair.Iv); keep = true;
                    FirmwareXml.OfpEntries(c, xml, page, pair.Key, pair.Iv); return;
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            finally { if (!keep) { CryptographicOperations.ZeroMemory(pair.Key); CryptographicOperations.ZeroMemory(pair.Iv); } }
        }
        throw new NotSupportedException(Strings.Unsupported);
    }
}
