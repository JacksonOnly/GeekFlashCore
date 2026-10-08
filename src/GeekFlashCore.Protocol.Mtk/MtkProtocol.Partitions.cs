using GeekFlashCore.Gpt;
using GeekFlashCore.Gpt.Abstractions;
using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol
{
    private byte[] ReadMetadata(MtkStorageRegion region, long offset, int length)
    {
        _ = Range(new(region.WireId, offset, length));
        byte[] bytes = new byte[length];
        using var output = new MemoryStream(bytes, true);
        try { _da!.Read(region, offset, length, output); return bytes; }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private sealed record GptPartitions(IReadOnlyList<GptEntry> Entries, MtkFlashRange Primary, MtkFlashRange Backup);

    private GptPartitions? ReadGpt(MtkStorageRegion region)
    {
        int block = region.BlockSize;
        if (block < 512 || region.Kind == MtkStorageKind.Nand || region.Length < 3L * block)
            return null;
        _logger.Debug(Strings.PartitionGptProbe, region.Kind, region.WireId, region.WireId == _storage!.UserRegionId);
        byte[] prefix = ReadMetadata(region, 0, 2 * block);
        bool primaryPresent = prefix.AsSpan(block, 8).SequenceEqual("EFI PART"u8);
        if (primaryPresent)
        {
            try { return ReadGptCopy(region, prefix.AsSpan(block).ToArray(), false); }
            catch (MtkResourceException) { /* Only metadata validation permits backup recovery. */ }
            catch (GptException) { }
        }
        byte[] backup = ReadMetadata(region, region.Length - block, block);
        if (!backup.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
        {
            if (primaryPresent)
                throw new MtkResourceException("GPT copies");
            return null;
        }
        try
        {
            var entries = ReadGptCopy(region, backup, true);
            _logger.ForContext("MtkSummary", true).Warning(Strings.GptBackupSelected, region.Name);
            return entries;
        }
        catch (GptException) { throw new MtkResourceException("GPT copies"); }
    }

    private GptPartitions ReadGptCopy(MtkStorageRegion region, byte[] header, bool backup)
    {
        var h = header.AsSpan();
        int block = region.BlockSize;
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
        ulong last = checked((ulong)(region.Length / block - 1));
        ulong current = BinaryPrimitives.ReadUInt64LittleEndian(h[24..]);
        ulong alternate = BinaryPrimitives.ReadUInt64LittleEndian(h[32..]);
        ulong firstUsable = BinaryPrimitives.ReadUInt64LittleEndian(h[40..]);
        ulong lastUsable = BinaryPrimitives.ReadUInt64LittleEndian(h[48..]);
        ulong entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(h[72..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(h[80..]);
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(h[84..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) != 0x10000 ||
            headerSize < 92 || headerSize > block || BinaryPrimitives.ReadUInt32LittleEndian(h[20..]) != 0 ||
            current != (backup ? last : 1) || alternate != (backup ? 1UL : last) ||
            firstUsable < 2 || firstUsable > lastUsable || lastUsable >= last ||
            count is 0 or > 4096 || entrySize is < 128 or > 4096 || entrySize % 8 != 0 || entriesLba > last)
            throw new MtkResourceException("GPT geometry");
        uint expectedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        h.Slice(16, 4).Clear();
        if (Crc32Helper.Compute(h[..(int)headerSize]) != expectedHeaderCrc)
            throw new MtkResourceException("GPT header CRC");
        long bytes = checked((long)count * entrySize);
        long padded = checked((bytes + block - 1) / block * block);
        ulong sectors = (ulong)(padded / block);
        if (padded > 1048576 - 2L * block || sectors > last - entriesLba ||
            (backup ? entriesLba <= lastUsable || entriesLba + sectors > current :
                entriesLba < 2 || entriesLba + sectors > firstUsable))
            throw new MtkResourceException("GPT entry range");
        byte[] entries = ReadMetadata(region, checked((long)entriesLba * block), checked((int)padded));
        if (Crc32Helper.Compute(entries.AsSpan(0, (int)bytes)) != BinaryPrimitives.ReadUInt32LittleEndian(h[88..]))
            throw new MtkResourceException("GPT entry CRC");

        // The original physical header and array have been validated independently.
        // Normalize only the parser's compact metadata layout; never modify device bytes.
        byte[] image = new byte[checked(2 * block + (int)padded)];
        h.CopyTo(image.AsSpan(block));
        var compact = image.AsSpan(block, block);
        BinaryPrimitives.WriteUInt64LittleEndian(compact[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(compact[32..], last);
        BinaryPrimitives.WriteUInt64LittleEndian(compact[72..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(compact[16..], Crc32Helper.Compute(compact[..(int)headerSize]));
        entries.CopyTo(image, 2 * block);
        var table = new GptParser().Parse(image, new GptParseOptions
        {
            SectorSize = block,
            CrcPolicy = GptCrcPolicy.Strict,
            AllowUnpatchedPartitionGeometry = false,
            AllowEmptyPartitionTypeId = false,
            SkipEmptyPartitionTypeId = true
        });
        if (table.Overlaps.Count != 0 || table.Entries.GroupBy(e => e.Id).Any(g => g.Key == Guid.Empty || g.Count() > 1))
            throw new MtkResourceException("GPT partition overlap/identity");
        // Expose only reserved areas outside the validated usable-LBA interval.
        // These boundaries also remain available when the backup supplied the table.
        var primaryRange = new MtkFlashRange(region.WireId, 0, checked((long)firstUsable * block));
        long backupOffset = checked((long)(lastUsable + 1) * block);
        var backupRange = new MtkFlashRange(region.WireId, backupOffset, checked(region.Length - backupOffset));
        _ = Range(primaryRange);
        _ = Range(backupRange);
        return new(table.Entries, primaryRange, backupRange);
    }
}
