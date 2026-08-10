using GeekFlashCore.Gpt.Internals;
using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.Gpt;

public class GptFactory
{
    private const uint GptRevision10 = 0x0001_0000;

    public IGpt Create(GptCreationOptions options) => Create([], options);

    public IGpt Create(
        IReadOnlyList<GptEntry> partitions,
        GptCreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(options);

        int sectorSize = GptFormatValidator.ValidateSectorSize(options.SectorSize);
        ValidateEntryGeometry(options);

        int entryCount = options.PartitionEntryCount;
        int entrySize = options.PartitionEntrySize;
        int entryCapacityBytes = GetEntryCapacityBytes(entryCount, entrySize, sectorSize);
        ulong entryArraySectors = checked((ulong)entryCapacityBytes / (ulong)sectorSize);
        ulong firstUsableLba = checked(2 + entryArraySectors);
        if (options.TotalDiskSectors < checked((2 * entryArraySectors) + 4))
            throw new ArgumentOutOfRangeException(nameof(options));

        ulong backupHeaderLba = options.TotalDiskSectors - 1;
        ulong backupEntriesLba = checked(backupHeaderLba - entryArraySectors);
        ulong lastUsableLba = backupEntriesLba - 1;
        Guid diskId = options.DiskId ?? Guid.NewGuid();
        if (diskId == Guid.Empty)
            throw new ArgumentException(null, nameof(options));

        ValidatePartitions(
            partitions,
            entryCount,
            entrySize,
            firstUsableLba,
            lastUsableLba);

        var entryStorage = new byte[entryCapacityBytes];
        var entries = new List<GptEntry>(partitions.Count);
        for (int index = 0; index < partitions.Count; index++)
        {
            GptEntry partition = partitions[index];
            GptCodec.WriteEntry(
                entryStorage.AsSpan(index * entrySize, entrySize),
                partition);
            entries.Add(new GptEntry(
                index + 1,
                index,
                partition.TypeId,
                partition.Id,
                partition.FirstLba,
                partition.LastLba,
                partition.Attributes,
                partition.Name));
        }

        uint entriesCrc = Crc32Helper.Compute(
            entryStorage.AsSpan(0, checked(entryCount * entrySize)));
        var commonHeader = new GptHeader(
            GptRevision10,
            GptCodec.MinimumHeaderSize,
            0,
            0,
            1,
            backupHeaderLba,
            firstUsableLba,
            lastUsableLba,
            diskId,
            2,
            checked((uint)entryCount),
            checked((uint)entrySize),
            entriesCrc);
        GptFormatValidator.ValidateHeaderStructure(commonHeader, sectorSize);

        (GptHeader primaryHeader, byte[] primaryBytes) =
            FinalizeHeader(commonHeader, sectorSize);
        (GptHeader backupHeader, byte[] backupBytes) = FinalizeHeader(
            commonHeader with
            {
                CurrentLba = backupHeaderLba,
                AlternateLba = 1,
                PartitionEntryLba = backupEntriesLba
            },
            sectorSize);

        var validCopy = new GptCopyStatus(true, true, true);
        int primaryEntriesOffset = checked(2 * sectorSize);
        int backupEntriesOffset = checked(primaryEntriesOffset + entryCapacityBytes);
        int backupHeaderOffset = checked(backupEntriesOffset + entryCapacityBytes);
        var primaryLayout = new GptHeaderLayout(
            GptHeaderCopy.Primary,
            sectorSize,
            primaryEntriesOffset,
            entryCapacityBytes,
            entryCount,
            primaryHeader,
            primaryBytes,
            entryStorage.ToArray(),
            validCopy);
        var backupLayout = new GptHeaderLayout(
            GptHeaderCopy.Backup,
            backupHeaderOffset,
            backupEntriesOffset,
            entryCapacityBytes,
            entryCount,
            backupHeader,
            backupBytes,
            entryStorage.ToArray(),
            validCopy);
        var layout = new GptLayout(
            GptImageType.Both,
            GptContainerType.Compact,
            sectorSize,
            checked(backupHeaderOffset + sectorSize),
            null,
            null,
            null,
            null,
            primaryLayout,
            backupLayout);

        return new GuidPartitionTable(
            layout,
            primaryLayout,
            primaryHeader,
            entries,
            entryStorage,
            options.IncludeUnallocatedRegions);
    }

    private static void ValidateEntryGeometry(GptCreationOptions options)
    {
        if (options.PartitionEntryCount is < 1 or > (int)GptFormatValidator.MaximumEntryCount)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.PartitionEntrySize < GptCodec.MinimumEntrySize ||
            (options.PartitionEntrySize & 7) != 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static void ValidatePartitions(
        IReadOnlyList<GptEntry> partitions,
        int entryCount,
        int entrySize,
        ulong firstUsableLba,
        ulong lastUsableLba)
    {
        if (partitions.Count > entryCount)
            throw new ArgumentException(null, nameof(partitions));

        var ids = new HashSet<Guid>();
        var geometry = new List<GptEntry>(partitions.Count);
        foreach (GptEntry partition in partitions)
        {
            ArgumentNullException.ThrowIfNull(partition);
            ArgumentNullException.ThrowIfNull(partition.Name);
            if (partition.TypeId == Guid.Empty ||
                partition.Id == Guid.Empty ||
                !ids.Add(partition.Id))
                throw new ArgumentException(null, nameof(partitions));
            if (partition.FirstLba > partition.LastLba ||
                partition.FirstLba < firstUsableLba ||
                partition.LastLba > lastUsableLba)
                throw new ArgumentOutOfRangeException(nameof(partitions));

            GptCodec.WriteEntry(new byte[entrySize], partition);
            geometry.Add(partition);
        }

        geometry.Sort(static (left, right) => left.FirstLba.CompareTo(right.FirstLba));
        for (int index = 1; index < geometry.Count; index++)
        {
            if (geometry[index].FirstLba <= geometry[index - 1].LastLba)
                throw new ArgumentException(null, nameof(partitions));
        }
    }

    private static int GetEntryCapacityBytes(int entryCount, int entrySize, int sectorSize)
    {
        long rawBytes = checked((long)entryCount * entrySize);
        long sectors = checked((rawBytes + sectorSize - 1) / sectorSize);
        return checked((int)(sectors * sectorSize));
    }

    private static (GptHeader Header, byte[] Bytes) FinalizeHeader(
        GptHeader header,
        int sectorSize)
    {
        var bytes = new byte[sectorSize];
        GptHeader withoutCrc = header with { HeaderCrc32 = 0 };
        GptCodec.WriteHeader(bytes, withoutCrc);
        uint crc = Crc32Helper.Compute(bytes.AsSpan(0, checked((int)header.HeaderSize)));
        GptHeader finalized = withoutCrc with { HeaderCrc32 = crc };
        GptCodec.WriteHeader(bytes, finalized);
        return (finalized, bytes);
    }
}