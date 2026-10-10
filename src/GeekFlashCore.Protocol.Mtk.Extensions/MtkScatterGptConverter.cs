using System.Text;
using System.Buffers.Binary;
using GeekFlashCore.Gpt;
using GeekFlashCore.Gpt.Abstractions;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Compact GPT images, never a materialized disk.</summary>
public sealed record MtkScatterGptImages(byte[] Primary, byte[] Backup);

/// <summary>Offline conversion of USER partition geometry. Scatter-only download policies are not inferred from GPT.</summary>
public static class MtkScatterGptConverter
{
    /// <summary>Builds strictly validated compact primary and backup GPT images for explicit observed storage.</summary>
    public static MtkScatterGptImages ToGpt(MtkScatterManifest manifest, MtkStorageInfo storage)
    {
        ArgumentNullException.ThrowIfNull(manifest); ArgumentNullException.ThrowIfNull(storage);
        if (storage.Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs)) throw new MtkCapabilityException("scatter GPT storage");
        // Preloader names are DA-managed mappings, even if a manifest places a placeholder in USER.
        // Keep GPT ranges for RESERVED/NEEDRESIZE planning, but never serialize them as entries.
        var userManifest = new MtkScatterManifest(manifest.Partitions.Where(p => p.Storage == storage.Kind && p.RegionId == storage.UserRegionId && !MtkPartitionNames.IsPreloader(p.Name)).ToArray());
        return Build(MtkScatterPlanBuilder.Create(userManifest, storage, 0).Partitions, storage);
    }
    internal static MtkScatterGptImages Build(IReadOnlyList<MtkScatterPlannedPartition> parts, MtkStorageInfo storage)
    {
        var user = User(storage);
        var data = parts.Where(p => p.Range.RegionId == user.WireId && !MtkPartitionNames.IsMapped(p.Name)).ToArray();
        if (data.Length == 0 || data.Length > 4096 || data.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Length)
            throw new MtkResourceException("scatter GPT partitions");
        foreach (var p in data)
            if (p.Range.Offset < 0 || p.Range.Length <= 0 || p.Range.Offset > user.Length - p.Range.Length ||
                p.Range.Offset % user.BlockSize != 0 || p.Range.Length % user.BlockSize != 0 || p.Name.Length is < 1 or > 36)
                throw new MtkResourceException("scatter GPT range/name");
        var entries = data.Select((p, i) => new GptEntry(i + 1, i, Guid.Parse("0FC63DAF-8483-4772-8E79-3D69D8477DE4"), Guid.NewGuid(),
            (ulong)(p.Range.Offset / user.BlockSize), (ulong)((p.Range.Offset + p.Range.Length) / user.BlockSize - 1), 0, p.Name)).ToArray();
        var table = new GptFactory().Create(entries, new() { SectorSize = user.BlockSize, TotalDiskSectors = (ulong)(user.Length / user.BlockSize), PartitionEntryCount = Math.Max(128, entries.Length) });
        return new(table.ToArray(new() { ImageType = GptImageType.Main, PreserveFullDiskImage = false }),
            table.ToArray(new() { ImageType = GptImageType.Backup, PreserveFullDiskImage = false }));
    }
    internal static bool IsMetadata(string name) => MtkPartitionNames.IsGpt(name);
    private static MtkStorageRegion User(MtkStorageInfo storage)
    {
        if (storage.Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs or MtkStorageKind.Sdmmc)) throw new MtkCapabilityException("scatter GPT storage");
        return storage.Regions.Single(r => r.WireId == storage.UserRegionId);
    }
    /// <summary>Exports geometry with downloads disabled and NONE filenames. Explicit capacity/platform must match the caller's device.</summary>
    public static string FromGpt(byte[] image, MtkStorageInfo storage, string platform)
    {
        ArgumentNullException.ThrowIfNull(image); ArgumentNullException.ThrowIfNull(storage);
        if (storage.Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs)) throw new MtkCapabilityException("scatter GPT storage");
        if (image.Length > 4 * 1024 * 1024 || platform is null || platform.Length is < 1 or > 64 || platform.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new MtkResourceException("GPT conversion input");
        var user = User(storage);
        var table = new GptParser().Parse(NormalizeObservedUfs(image, user), new() { SectorSize = user.BlockSize, CrcPolicy = GptCrcPolicy.Strict,
            AllowUnpatchedPartitionGeometry = false, AllowEmptyPartitionTypeId = false, SkipEmptyPartitionTypeId = true });
        if (table.TotalDiskSectors != (ulong)(user.Length / user.BlockSize) || table.Overlaps.Count != 0 ||
            table.Entries.Count == 0 || table.Entries.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != table.Entries.Count)
            throw new MtkResourceException("GPT conversion geometry");
        var text = new StringBuilder();
        text.AppendLine("- general: MTK_PLATFORM_CFG").AppendLine("  info:").AppendLine("    - config_version: V1.1.2")
            .AppendLine($"      platform: {platform}").AppendLine($"      storage: {(storage.Kind == MtkStorageKind.Emmc ? "HW_STORAGE_EMMC" : "HW_STORAGE_UFS")}")
            .AppendLine($"      block_size: 0x{user.BlockSize:X}");
        foreach (var p in table.Entries)
        {
            if (p.Name.Length is < 1 or > 36 || p.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
                throw new MtkResourceException("GPT conversion partition name");
            text.AppendLine($"- partition_index: SYS{p.SlotIndex}").AppendLine($"  partition_name: {p.Name}")
                .AppendLine("  file_name: NONE").AppendLine("  is_download: false").AppendLine("  type: NORMAL_ROM")
                .AppendLine($"  linear_start_addr: 0x{checked(p.FirstLba * (ulong)user.BlockSize):X}")
                .AppendLine($"  physical_start_addr: 0x{checked(p.FirstLba * (ulong)user.BlockSize):X}")
                .AppendLine($"  partition_size: 0x{checked(p.SectorCount * (ulong)user.BlockSize):X}")
                .AppendLine($"  region: {(storage.Kind == MtkStorageKind.Emmc ? "EMMC_USER" : "UFS_LU2")}")
                .AppendLine("  operation_type: UPDATE");
        }
        return text.ToString();
    }

    private static byte[] NormalizeObservedUfs(byte[] image, MtkStorageRegion user)
    {
        const int block = 4096, arrayBytes = 128 * 128;
        if (user.Kind != MtkStorageKind.Ufs || user.BlockSize != block || image.Length < 5 * block || image.Length % block != 0) return image;
        ulong last = (ulong)(user.Length / block - 1);
        bool primary = image.AsSpan(block, 8).SequenceEqual("EFI PART"u8) && BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(block + 24)) == 1;
        bool backup = image.AsSpan(image.Length - block, 8).SequenceEqual("EFI PART"u8) && BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(image.Length - block + 24)) == last;
        if (primary && backup) throw new MtkResourceException("ambiguous compact GPT");
        if (!primary && !backup) return image;
        int at = primary ? block : image.Length - block;
        var header = image.AsSpan(at, block);
        if (BinaryPrimitives.ReadUInt64LittleEndian(header[40..]) != 34 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[80..]) != 128 || BinaryPrimitives.ReadUInt32LittleEndian(header[84..]) != 128) return image;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        ulong entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]), lastUsable = BinaryPrimitives.ReadUInt64LittleEndian(header[48..]);
        if (size is < 92 or > block || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 0x10000 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != 0 || lastUsable < 34 || lastUsable >= last ||
            BinaryPrimitives.ReadUInt64LittleEndian(header[32..]) != (primary ? last : 1UL) ||
            (primary ? entriesLba != 2 : entriesLba != lastUsable + 1 || entriesLba > last || last - entriesLba < 4))
            throw new MtkResourceException("legacy UFS GPT geometry");
        byte[] copyHeader = header.ToArray();
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]); copyHeader.AsSpan(16, 4).Clear();
        if (Crc32Helper.Compute(copyHeader.AsSpan(0, (int)size)) != crc) throw new MtkResourceException("legacy UFS GPT header CRC");
        ulong imageSectors = (ulong)(image.Length / block);
        if (imageSectors - 1 > last) throw new MtkResourceException("legacy UFS GPT image range");
        ulong imageStart = primary ? 0UL : last - (imageSectors - 1);
        if (entriesLba < imageStart || entriesLba - imageStart > (ulong)(image.Length - arrayBytes) / block)
            throw new MtkResourceException("legacy UFS GPT array range");
        int offset = checked((int)(entriesLba - imageStart) * block);
        if (Crc32Helper.Compute(image.AsSpan(offset, arrayBytes)) != BinaryPrimitives.ReadUInt32LittleEndian(header[88..]))
            throw new MtkResourceException("legacy UFS GPT array CRC");
        byte[] normalized = (byte[])image.Clone();
        var output = normalized.AsSpan(at, block);
        BinaryPrimitives.WriteUInt64LittleEndian(output[40..], 6); output.Slice(16, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(output[16..], Crc32Helper.Compute(output[..(int)size]));
        return normalized;
    }
}
