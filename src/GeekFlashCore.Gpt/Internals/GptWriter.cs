using System.Buffers;
using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.Gpt.Internals;


internal static class GptWriter
{
    private const int SgdiskBlockSize = 512;
    private static ReadOnlySpan<byte> ProtectiveMbrEntryData =>
    [
        0x01, 0x00, 0xEE, 0xFF, 0xFF, 0xFF, 0x01,
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF
    ];

    public static int GetOutputLength(
        GptWriteSnapshot snapshot,
        GptImageType imageType,
        bool preserveFullDisk)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (preserveFullDisk)
        {
            if (snapshot.Layout.ContainerType != GptContainerType.FullDisk ||
                snapshot.Layout.SourceImage is null && snapshot.Layout.SourcePath is null)
                throw new InvalidOperationException(Strings.PreservedFullDiskRequired);
            if (snapshot.Layout.SourceLength > int.MaxValue)
                throw new NotSupportedException(Strings.FullDiskTooLargeForArray);
            return checked((int)snapshot.Layout.SourceLength);
        }

        int capacityBytes = GetCapacityBytes(
            snapshot.Layout.SectorSize,
            snapshot.AvailableEntryCount,
            snapshot.Header.PartitionEntrySize);
        return imageType switch
        {
            GptImageType.Main => checked((2 * snapshot.Layout.SectorSize) + capacityBytes),
            GptImageType.Backup => checked(capacityBytes + snapshot.Layout.SectorSize),
            GptImageType.Both => checked(
                (3 * snapshot.Layout.SectorSize) + (2 * capacityBytes)),
            GptImageType.SgdiskBackup => checked(
                (3 * SgdiskBlockSize) +
                GptFormatValidator.GetEntryArrayLength(snapshot.Header)),
            GptImageType.Unknown => throw new NotSupportedException(
                Strings.FormatImageTypeCannotBeExported(imageType)),
            _ => throw new ArgumentOutOfRangeException(nameof(imageType))
        };
    }

    public static void Write(
        Span<byte> destination,
        GptWriteSnapshot snapshot,
        GptImageType imageType,
        bool preserveFullDisk)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int outputLength = GetOutputLength(snapshot, imageType, preserveFullDisk);
        if (destination.Length < outputLength)
            throw new ArgumentException(Strings.DestinationTooSmall, nameof(destination));
        destination = destination[..outputLength];

        if (preserveFullDisk)
        {
            WriteFullDisk(destination, snapshot);
            return;
        }
        if (imageType == GptImageType.SgdiskBackup)
        {
            WriteSgdiskBackup(destination, snapshot);
            return;
        }
        WriteCompact(destination, snapshot, imageType);
    }

    public static void WritePreservedFullDisk(
        Stream destination,
        GptWriteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyList<GptWriteRegion> regions = CreateFullDiskWriteRegions(snapshot);

        destination.Seek(0, SeekOrigin.Begin);
        if (snapshot.Layout.SourceImage is byte[] sourceImage)
        {
            destination.Write(sourceImage);
        }
        else
        {
            using FileStream source = OpenFullDiskSource(snapshot.Layout);
            source.CopyTo(destination, 1024 * 1024);
        }
        destination.SetLength(snapshot.Layout.SourceLength);
        foreach (GptWriteRegion region in regions)
        {
            destination.Seek(region.Offset, SeekOrigin.Begin);
            destination.Write(region.Bytes.Span);
        }
    }

    private static void WriteCompact(
        Span<byte> destination,
        GptWriteSnapshot snapshot,
        GptImageType imageType)
    {
        int sectorSize = snapshot.Layout.SectorSize;
        int capacityBytes = GetCapacityBytes(
            sectorSize,
            snapshot.AvailableEntryCount,
            snapshot.Header.PartitionEntrySize);
        ulong capacitySectors = (ulong)(capacityBytes / sectorSize);
        ulong backupLba = ResolveBackupLba(snapshot.Header);
        if (imageType == GptImageType.Both && backupLba <= 1)
            throw new InvalidOperationException(Strings.BackupExportRequiresGeometry);

        destination.Clear();
        int mainEntriesOffset = 2 * sectorSize;
        int backupEntriesOffset = imageType == GptImageType.Both
            ? checked(mainEntriesOffset + capacityBytes)
            : 0;
        int backupHeaderOffset = imageType == GptImageType.Both
            ? checked(backupEntriesOffset + capacityBytes)
            : capacityBytes;

        Span<byte> primaryEntries = imageType == GptImageType.Backup
            ? destination.Slice(backupEntriesOffset, capacityBytes)
            : destination.Slice(mainEntriesOffset, capacityBytes);
        WriteEntryStorage(primaryEntries, snapshot);
        uint entriesCrc = ComputeEntryArrayCrc(primaryEntries, snapshot.Header);

        if (imageType is GptImageType.Main or GptImageType.Both)
        {
            WriteProtectiveMbr(destination[..sectorSize]);
            GptHeader primaryHeader = snapshot.Header with
            {
                CurrentLba = 1,
                AlternateLba = backupLba > 1 ? backupLba : 0,
                PartitionEntryLba = 2,
                PartitionEntryArrayCrc32 = entriesCrc
            };
            WriteHeaderSector(
                destination.Slice(sectorSize, sectorSize),
                primaryHeader,
                snapshot.Layout.MainHeader?.HeaderBytes ?? snapshot.HeaderTemplate);
        }

        if (imageType is GptImageType.Backup or GptImageType.Both)
        {
            if (imageType == GptImageType.Both)
                primaryEntries.CopyTo(destination.Slice(backupEntriesOffset, capacityBytes));

            GptHeader backupHeader;
            if (backupLba > 1)
            {
                if (backupLba <= capacitySectors)
                    throw new InvalidOperationException(Strings.BackupExportRequiresGeometry);
                backupHeader = snapshot.Header with
                {
                    CurrentLba = backupLba,
                    AlternateLba = 1,
                    PartitionEntryLba = checked(backupLba - capacitySectors),
                    PartitionEntryArrayCrc32 = entriesCrc
                };
            }
            else if (imageType == GptImageType.Backup)
            {
                backupHeader = snapshot.Header with
                {
                    CurrentLba = 0,
                    AlternateLba = 1,
                    PartitionEntryLba = 0,
                    PartitionEntryArrayCrc32 = entriesCrc
                };
            }
            else
            {
                throw new InvalidOperationException(Strings.BackupExportRequiresGeometry);
            }
            WriteHeaderSector(
                destination.Slice(backupHeaderOffset, sectorSize),
                backupHeader,
                snapshot.Layout.BackupHeader?.HeaderBytes ?? snapshot.HeaderTemplate);
        }
    }

    private static void WriteSgdiskBackup(
        Span<byte> destination,
        GptWriteSnapshot snapshot)
    {
        ulong backupLba = ResolveBackupLba(snapshot.Header);
        if (backupLba <= 1)
            throw new InvalidOperationException(Strings.BackupExportRequiresGeometry);
        int declaredBytes = GptFormatValidator.GetEntryArrayLength(snapshot.Header);
        ulong capacitySectors = GptFormatValidator.GetPhysicalEntryArraySectorCount(
            snapshot.AvailableEntryCount,
            snapshot.Header.PartitionEntrySize,
            snapshot.Layout.SectorSize);
        if (backupLba <= capacitySectors)
            throw new InvalidOperationException(Strings.BackupExportRequiresGeometry);

        destination.Clear();
        WriteProtectiveMbr(destination[..SgdiskBlockSize]);
        Span<byte> entries = destination.Slice(3 * SgdiskBlockSize, declaredBytes);
        snapshot.EntryStorage.AsSpan(0, declaredBytes).CopyTo(entries);
        uint entriesCrc = Crc32Helper.Compute(entries);

        GptHeader primaryHeader = snapshot.Header with
        {
            CurrentLba = 1,
            AlternateLba = backupLba,
            PartitionEntryLba = 2,
            PartitionEntryArrayCrc32 = entriesCrc
        };
        GptHeader backupHeader = snapshot.Header with
        {
            CurrentLba = backupLba,
            AlternateLba = 1,
            PartitionEntryLba = checked(backupLba - capacitySectors),
            PartitionEntryArrayCrc32 = entriesCrc
        };
        WriteHeaderSector(
            destination.Slice(SgdiskBlockSize, SgdiskBlockSize),
            primaryHeader,
            snapshot.Layout.MainHeader?.HeaderBytes ?? snapshot.HeaderTemplate);
        WriteHeaderSector(
            destination.Slice(2 * SgdiskBlockSize, SgdiskBlockSize),
            backupHeader,
            snapshot.Layout.BackupHeader?.HeaderBytes ?? snapshot.HeaderTemplate);
    }

    private static void WriteFullDisk(
        Span<byte> destination,
        GptWriteSnapshot snapshot)
    {
        byte[] source = snapshot.Layout.SourceImage ??
            throw new InvalidOperationException(Strings.FullDiskBytesMissing);
        source.CopyTo(destination);
        foreach (GptWriteRegion region in CreateFullDiskWriteRegions(snapshot))
            region.Bytes.Span.CopyTo(destination.Slice(
                checked((int)region.Offset),
                region.Bytes.Length));
    }

    private static IReadOnlyList<GptWriteRegion> CreateFullDiskWriteRegions(
        GptWriteSnapshot snapshot)
    {
        if (snapshot.Layout.ContainerType != GptContainerType.FullDisk ||
            snapshot.Layout.SourceImage is null && snapshot.Layout.SourcePath is null)
            throw new InvalidOperationException(Strings.PreservedFullDiskRequired);

        var regions = new List<GptWriteRegion>(5);
        ulong backupLba = ResolveBackupLba(snapshot.Header);
        ulong diskSectorCount = (ulong)snapshot.Layout.SourceLength /
                                (ulong)snapshot.Layout.SectorSize;
        if (backupLba > 1 && backupLba + 1 > diskSectorCount)
            throw new InvalidOperationException(Strings.GeometryContainerMismatch);
        var updatedMbr = new byte[snapshot.Layout.SectorSize];
        WriteProtectiveMbr(updatedMbr);
        regions.Add(new GptWriteRegion(0, updatedMbr));

        uint entriesCrc = Crc32Helper.Compute(snapshot.EntryStorage.AsSpan(
            0,
            GptFormatValidator.GetEntryArrayLength(snapshot.Header)));
        if (snapshot.Layout.MainHeader is GptHeaderLayout primary)
        {
            if (primary.HeaderOffset / snapshot.Layout.SectorSize != 1 ||
                primary.EntriesOffset / snapshot.Layout.SectorSize !=
                checked((long)primary.Header.PartitionEntryLba))
                throw new InvalidOperationException(Strings.GeometryContainerMismatch);
            var entries = GC.AllocateUninitializedArray<byte>(primary.CapacityBytes);
            WriteEntryStorage(entries, snapshot);
            regions.Add(new GptWriteRegion(primary.EntriesOffset, entries));
            GptHeader header = snapshot.Header with
            {
                CurrentLba = 1,
                AlternateLba = backupLba > 1 ? backupLba : 0,
                PartitionEntryLba = primary.Header.PartitionEntryLba,
                PartitionEntryArrayCrc32 = entriesCrc
            };
            byte[] headerBytes = CreateHeaderSector(
                snapshot.Layout.SectorSize,
                header,
                primary.HeaderBytes);
            regions.Add(new GptWriteRegion(primary.HeaderOffset, headerBytes));
        }
        if (snapshot.Layout.BackupHeader is GptHeaderLayout backup)
        {
            if (backupLba <= 1)
                throw new InvalidOperationException(Strings.UnpatchedBackupInFullDisk);
            if ((ulong)(backup.HeaderOffset / snapshot.Layout.SectorSize) != backupLba ||
                (ulong)(backup.EntriesOffset / snapshot.Layout.SectorSize) !=
                backup.Header.PartitionEntryLba)
                throw new InvalidOperationException(Strings.GeometryContainerMismatch);
            var entries = GC.AllocateUninitializedArray<byte>(backup.CapacityBytes);
            WriteEntryStorage(entries, snapshot);
            regions.Add(new GptWriteRegion(backup.EntriesOffset, entries));
            GptHeader header = snapshot.Header with
            {
                CurrentLba = backupLba,
                AlternateLba = 1,
                PartitionEntryLba = backup.Header.PartitionEntryLba,
                PartitionEntryArrayCrc32 = entriesCrc
            };
            byte[] headerBytes = CreateHeaderSector(
                snapshot.Layout.SectorSize,
                header,
                backup.HeaderBytes);
            regions.Add(new GptWriteRegion(backup.HeaderOffset, headerBytes));
        }

        GptWriteRegion[] ordered = regions.OrderBy(static region => region.Offset).ToArray();
        ValidateFullDiskWriteRegions(ordered, snapshot.Layout.SourceLength);
        return ordered;
    }

    private static byte[] CreateHeaderSector(
        int sectorSize,
        GptHeader header,
        ReadOnlySpan<byte> template)
    {
        var sector = GC.AllocateUninitializedArray<byte>(sectorSize);
        sector.AsSpan().Clear();
        WriteHeaderSector(sector, header, template);
        return sector;
    }

    private static void ValidateFullDiskWriteRegions(
        IReadOnlyList<GptWriteRegion> regions,
        long sourceLength)
    {
        long previousEnd = 0;
        foreach (GptWriteRegion region in regions)
        {
            if (region.Offset < previousEnd || region.Offset < 0 ||
                region.Offset > sourceLength - region.Bytes.Length)
                throw new InvalidOperationException(Strings.GeometryContainerMismatch);
            previousEnd = checked(region.Offset + region.Bytes.Length);
        }
    }

    private static FileStream OpenFullDiskSource(GptLayout layout)
    {
        string path = layout.SourcePath ??
            throw new InvalidOperationException(Strings.FullDiskBytesMissing);
        var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        if (source.Length != layout.SourceLength ||
            layout.SourceLastWriteTimeUtc is DateTime expectedWriteTime &&
            File.GetLastWriteTimeUtc(path) != expectedWriteTime)
        {
            source.Dispose();
            throw new InvalidOperationException(Strings.FullDiskSourceChanged);
        }
        if (!SourceSnapshotMatches(source, layout))
        {
            source.Dispose();
            throw new InvalidOperationException(Strings.FullDiskSourceChanged);
        }
        source.Seek(0, SeekOrigin.Begin);
        return source;
    }

    private static bool SourceSnapshotMatches(FileStream source, GptLayout layout)
    {
        if (layout.ProtectiveMbr is byte[] mbr &&
            !SourceRegionMatches(source, 0, mbr))
            return false;
        foreach (GptHeaderLayout header in EnumerateHeaders(layout))
        {
            if (!SourceRegionMatches(source, header.HeaderOffset, header.HeaderBytes) ||
                !SourceRegionMatches(source, header.EntriesOffset, header.EntryStorage))
                return false;
        }
        return true;
    }

    private static IEnumerable<GptHeaderLayout> EnumerateHeaders(GptLayout layout)
    {
        if (layout.MainHeader is not null) yield return layout.MainHeader;
        if (layout.BackupHeader is not null) yield return layout.BackupHeader;
    }

    private static bool SourceRegionMatches(
        FileStream source,
        long offset,
        ReadOnlySpan<byte> expected)
    {
        if (expected.IsEmpty) return true;
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Min(expected.Length, 64 * 1024));
        try
        {
            source.Seek(offset, SeekOrigin.Begin);
            int consumed = 0;
            while (consumed < expected.Length)
            {
                int length = Math.Min(rented.Length, expected.Length - consumed);
                source.ReadExactly(rented.AsSpan(0, length));
                if (!rented.AsSpan(0, length).SequenceEqual(expected.Slice(consumed, length)))
                    return false;
                consumed += length;
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteEntryStorage(
        Span<byte> destination,
        GptWriteSnapshot snapshot)
    {
        int declaredBytes = GptFormatValidator.GetEntryArrayLength(snapshot.Header);
        if (destination.Length < declaredBytes || snapshot.EntryStorage.Length < declaredBytes)
            throw new InvalidOperationException(Strings.EntrySlotExceedsOutputCapacity);
        int copyLength = Math.Min(destination.Length, snapshot.EntryStorage.Length);
        snapshot.EntryStorage.AsSpan(0, copyLength).CopyTo(destination);
        if (copyLength < destination.Length) destination[copyLength..].Clear();
    }

    private static uint ComputeEntryArrayCrc(
        ReadOnlySpan<byte> storage,
        GptHeader header) =>
        Crc32Helper.Compute(storage[..GptFormatValidator.GetEntryArrayLength(header)]);

    private static void WriteHeaderSector(
        Span<byte> sector,
        GptHeader header,
        ReadOnlySpan<byte> template)
    {
        int copyLength = Math.Min(sector.Length, template.Length);
        template[..copyLength].CopyTo(sector);
        GptHeader withoutCrc = header with { HeaderCrc32 = 0 };
        GptCodec.WriteHeader(sector, withoutCrc);
        int headerSize = checked((int)header.HeaderSize);
        if (headerSize > sector.Length)
            throw new InvalidDataException(Strings.HeaderExceedsSectorSize);
        uint crc = Crc32Helper.Compute(sector[..headerSize]);
        GptCodec.WriteHeader(sector, withoutCrc with { HeaderCrc32 = crc });
    }

    private static void WriteProtectiveMbr(Span<byte> sector)
    {
        if (sector.Length < 512)
            throw new ArgumentException(
                Strings.ProtectiveMbrRequiresSector,
                nameof(sector));
        sector.Clear();
        ProtectiveMbrEntryData.CopyTo(sector[448..]);
        sector[510] = 0x55;
        sector[511] = 0xAA;
    }

    private static int GetCapacityBytes(
        int sectorSize,
        int availableEntryCount,
        uint entrySize)
    {
        long rawBytes = checked((long)availableEntryCount * entrySize);
        long sectors = checked((rawBytes + sectorSize - 1) / sectorSize);
        return checked((int)(sectors * sectorSize));
    }

    private static ulong ResolveBackupLba(GptHeader header) =>
        Math.Max(header.CurrentLba, header.AlternateLba);
}

internal sealed record GptWriteRegion(long Offset, ReadOnlyMemory<byte> Bytes);
