// SPDX-License-Identifier: AGPL-3.0-or-later
// EMI framing: B. Kerler, bkerler/mtkclient, 2018-2024, GPLv3.
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Loaders;

/// <summary>Extracts validated EMI windows without materializing the preloader.</summary>
public static class MtkEmiParser
{
    public static MtkEmiImage Parse(IDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using Stream stream = source.OpenStream();
        if (!stream.CanRead || !stream.CanSeek || stream.Length != source.Length || source.Length < 20)
            throw new MtkResourceException("preloader");
        long origin = 0, length = source.Length;
        long mmm = Find(stream, 0, Math.Min(length, 65536), [0x4d, 0x4d, 0x4d, 1, 0x38, 0, 0, 0]);
        Span<byte> word = stackalloc byte[4];
        if (mmm >= 0)
        {
            if (mmm > source.Length - 0x30)
                throw new MtkResourceException("MMM header");
            stream.Position = mmm + 0x20;
            stream.ReadExactly(word);
            uint declared = BinaryPrimitives.ReadUInt32LittleEndian(word);
            stream.Position = mmm + 0x2c;
            stream.ReadExactly(word);
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (signature >= declared || declared < 0x34 || mmm > source.Length - declared)
                throw new MtkResourceException("MMM length");
            long end = checked(mmm + declared - signature);
            stream.Position = end - 4;
            stream.ReadExactly(word);
            uint dram = BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (dram == 0)
            {
                end -= 0x800;
                if (end < mmm + 0x34)
                    throw new MtkResourceException("MMM tail");
                stream.Position = end - 4;
                stream.ReadExactly(word);
                dram = BinaryPrimitives.ReadUInt32LittleEndian(word);
            }
            if (dram is < 20 or > 1048576 || dram > end - mmm - 4)
                throw new MtkResourceException("DRAM length");
            origin = end - dram - 4;
            length = dram;
        }
        if (length > 1048576)
            throw new MtkResourceException("EMI window");
        long info = Find(stream, origin, length, "MTK_BLOADER_INFO_v"u8);
        if (info < 0 || info > origin + length - 20)
            throw new MtkResourceException("BLOADER_INFO");
        stream.Position = info + 18;
        Span<byte> version = stackalloc byte[2];
        stream.ReadExactly(version);
        if (version[0] is < (byte)'0' or > (byte)'9' || version[1] is < (byte)'0' or > (byte)'9')
            throw new MtkResourceException("EMI version");
        uint number = (uint)((version[0] - '0') * 10 + version[1] - '0');
        // XFlash consumes the entire BLOADER window; Legacy consumes the MTK_BIN body.
        // Never discard the header merely because this window also contains MTK_BIN.
        var bloader = new MtkDataWindow(source, info, origin + length - info);
        long bin = Find(stream, info + 20, origin + length - info - 20, "MTK_BIN"u8);
        if (bin >= 0)
        {
            long start = checked(bin + 12);
            if (start >= origin + length)
                throw new MtkResourceException("MTK_BIN");
            length = origin + length - start;
            origin = start;
        }
        else if (info != origin)
            throw new MtkResourceException("EMI origin");
        return new(new MtkDataWindow(source, origin, length), number) { BloaderInfoSource = bloader };
    }
    private static long Find(Stream source, long offset, long length, ReadOnlySpan<byte> pattern)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        int overlap = 0;
        long processed = 0;
        try
        {
            source.Position = offset;
            while (processed < length)
            {
                int n = (int)Math.Min(65536 - overlap, length - processed);
                source.ReadExactly(buffer.AsSpan(overlap, n));
                int found = buffer.AsSpan(0, overlap + n).IndexOf(pattern);
                if (found >= 0)
                    return offset + processed - overlap + found;
                int total = overlap + n;
                processed += n;
                overlap = Math.Min(pattern.Length - 1, total);
                buffer.AsSpan(total - overlap, overlap).CopyTo(buffer);
            }
            return -1;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
}
