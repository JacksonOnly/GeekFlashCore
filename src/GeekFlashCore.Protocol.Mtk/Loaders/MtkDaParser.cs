// SPDX-License-Identifier: AGPL-3.0-or-later
// DA metadata layout: B. Kerler, bkerler/mtkclient, 2018-2024, GPLv3.
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Loaders;

/// <summary>Bounded metadata parser for D8/DC download-agent tables.</summary>
public static class MtkDaParser
{
    public static IReadOnlyList<MtkDaEntry> Parse(IDataSource source, MtkDaKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        using Stream stream = source.OpenStream();
        if (!stream.CanRead || !stream.CanSeek || stream.Length != source.Length || stream.Length < 0x6c)
            throw new MtkResourceException("DA container");
        Span<byte> header = stackalloc byte[0x6c];
        stream.ReadExactly(header);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[0x68..]);
        if (count is 0 or > 4096)
            throw new MtkResourceException("DA count");
        bool v6 = header[..0x68].IndexOf("MTK_DA_v6"u8) >= 0;
        bool legacy = false;
        if (count > 1 && stream.Length >= 0x6c + 0xd8 + 2)
        {
            stream.Position = 0x6c + 0xd8;
            Span<byte> magic = stackalloc byte[2];
            stream.ReadExactly(magic);
            legacy = BinaryPrimitives.ReadUInt16LittleEndian(magic) == 0xdada;
        }
        if (count == 1)
        {
            stream.Position = 0x6c;
            Span<byte> metadata = stackalloc byte[20];
            stream.ReadExactly(metadata);
            // Container layout and DA dialect are independent. Probe the count fields.
            legacy = Read16(metadata, 18) == 0 && Read16(metadata, 14) is >= 2 and <= 10;
        }
        int stride = legacy ? 0xd8 : 0xdc;
        if (checked(0x6cL + count * stride) > source.Length)
            throw new MtkResourceException("DA table");
        var entries = new List<MtkDaEntry>((int)count);
        Span<byte> entry = stackalloc byte[0xdc];
        for (int i = 0; i < count; i++)
        {
            stream.Position = 0x6cL + (long)i * stride;
            stream.ReadExactly(entry[..stride]);
            if (Read16(entry, 0) != 0xdada)
                throw new MtkResourceException("DA entry magic");
            int tableOffset = legacy ? 0x10 : 0x14;
            ushort rawIndex = Read16(entry, tableOffset - 4), regions = Read16(entry, tableOffset - 2);
            // Standard containers include file-info (or a DA1 alias) in region zero.
            // Both reference loaders execute region 1 then 2 even when the raw index is zero.
            ushort index = rawIndex == 0 && regions >= 3 ? (ushort)1 : rawIndex;
            if (regions is < 2 or > 10 || index >= regions - 1 || tableOffset + regions * 20 > stride)
                throw new MtkResourceException("DA region count/index");
            var windows = new List<MtkDaRegion>(regions);
            for (int j = 0; j < regions; j++)
            {
                ReadOnlySpan<byte> r = entry.Slice(tableOffset + j * 20, 20);
                uint offset = Read32(r, 0), length = Read32(r, 4), address = Read32(r, 8),
                    entryOffset = Read32(r, 12), signature = Read32(r, 16);
                if (length == 0 && j < index && offset == 0 && address == 0 && entryOffset == 0 && signature == 0)
                {
                    windows.Add(new(0, 0, 0, 0, 0));
                    continue;
                }
                if (length == 0 || signature >= length || (long)offset > source.Length - length ||
                    (ulong)address + length > (ulong)uint.MaxValue + 1 || entryOffset > length)
                    throw new MtkResourceException("DA region window");
                windows.Add(new(offset, length, address, entryOffset, signature));
            }
            MtkDaKind dialect = v6 ? MtkDaKind.Xml : kind ?? (legacy ? MtkDaKind.Legacy : MtkDaKind.XFlash);
            entries.Add(new(Read16(entry, 2), Read16(entry, 4), Read16(entry, 6),
                legacy ? (ushort)0 : Read16(entry, 8), index, dialect, windows.AsReadOnly()) { RawEntryRegionIndex = rawIndex });
        }
        return entries.AsReadOnly();
    }
    public static MtkDaImage Select(IDataSource source, MtkTargetInfo target, MtkDaKind? kind = null, ushort? alias = null)
    {
        var matches = Parse(source, kind).Where(e => e.HardwareCode == MtkChipCatalog.GetDaHardwareCode(target, alias) &&
            (e.HardwareSubCode == 0 || e.HardwareSubCode == target.HardwareSubCode) &&
            e.HardwareVersion <= target.HardwareVersion && e.SoftwareVersion <= target.SoftwareVersion &&
            (kind is null || e.Kind == kind)).ToArray();
        if (matches.Length == 0)
            throw new MtkResourceException("matching DA");
        ushort hw = matches.Max(e => e.HardwareVersion);
        ushort sw = matches.Where(e => e.HardwareVersion == hw).Max(e => e.SoftwareVersion);
        var best = matches.Where(e => e.HardwareVersion == hw && e.SoftwareVersion == sw).ToArray();
        if (best.Length != 1)
            throw new MtkResourceException("ambiguous DA");
        var selected = best[0];
        if (kind is null && selected.Kind == MtkDaKind.XFlash)
        {
            var da2 = selected.Regions[selected.EntryRegionIndex + 1];
            if (da2.Length > 16777216)
                throw new MtkResourceException("explicit DA dialect for large DA2");
            using var stream = new MtkDataWindow(source, da2.FileOffset, da2.Length).OpenStream();
            if (ContainsLegacyMarker(stream))
                selected = selected with
                {
                    Kind = MtkDaKind.Legacy
                };
        }
        return new(source, selected);
    }
    private static bool ContainsLegacyMarker(Stream stream)
    {
        ReadOnlySpan<byte> marker = "AND_SECRO_v"u8;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        int overlap = 0;
        try
        {
            while (stream.Position < stream.Length)
            {
                int n = (int)Math.Min(65536 - overlap, stream.Length - stream.Position);
                stream.ReadExactly(buffer.AsSpan(overlap, n));
                int total = overlap + n;
                if (buffer.AsSpan(0, total).IndexOf(marker) >= 0)
                    return true;
                overlap = Math.Min(marker.Length - 1, total);
                buffer.AsSpan(total - overlap, overlap).CopyTo(buffer);
            }
            return false;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    private static ushort Read16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint Read32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
