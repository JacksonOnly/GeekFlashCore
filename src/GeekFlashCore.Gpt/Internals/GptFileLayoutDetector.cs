using System.Diagnostics.CodeAnalysis;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;

namespace GeekFlashCore.Gpt.Internals;

internal static class GptFileLayoutDetector
{
    private const int SgdiskBlockSize = 512;
    private static ReadOnlySpan<byte> Signature => "EFI PART"u8;

    public static bool TryDetectFullDisk(
        IReadableBlockDevice stream,
        string sourcePath,
        int? sectorSize,
        bool headerOnly,
        [NotNullWhen(true)] out GptLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (LooksLikeSgdiskContainer(stream))
        {
            layout = null;
            return false;
        }

        GptLayout detected = Detect(
            stream,
            sourcePath,
            sectorSize,
            headerOnly);
        if (!IsFullDiskContainer(stream, detected))
        {
            layout = null;
            return false;
        }

        layout = detected;
        return true;
    }

    public static GptLayout Detect(
        IReadableBlockDevice stream,
        string sourcePath,
        int? sectorSize,
        bool headerOnly)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        long sourceLength = stream.Length;
        if (sourceLength < GptCodec.MinimumHeaderSize)
            throw new InvalidDataException(Strings.ImageTooSmallForHeader);

        (int SectorSize, FileHeaderCandidate? Primary) resolved =
            ResolvePrimary(stream, sourceLength, sectorSize);
        FileHeaderCandidate? primaryCandidate = resolved.Primary;
        FileHeaderCandidate? backupCandidate;
        if (primaryCandidate is not null)
        {
            backupCandidate = ReadDeclaredBackup(
                stream,
                sourceLength,
                resolved.SectorSize,
                primaryCandidate);
            backupCandidate ??= TryFindBackupNearEnd(
                stream,
                sourceLength,
                resolved.SectorSize);
        }
        else
        {
            backupCandidate = FindBackupNearEnd(
                stream,
                sourceLength,
                sectorSize,
                out resolved);
        }

        GptHeaderLayout? primary = TryMapHeader(
            stream,
            sourceLength,
            resolved.SectorSize,
            primaryCandidate,
            GptHeaderCopy.Primary,
            headerOnly);
        GptHeaderLayout? backup = TryMapHeader(
            stream,
            sourceLength,
            resolved.SectorSize,
            backupCandidate,
            GptHeaderCopy.Backup,
            headerOnly);
        if (primary is null && backup is null)
            throw new InvalidDataException(Strings.LayoutHasNoHeader);

        byte[] protectiveMbr = ReadBytes(stream, 0, resolved.SectorSize);
        return new GptLayout(
            primary is not null && backup is not null
                ? GptImageType.Both
                : primary is not null
                    ? GptImageType.Main
                    : GptImageType.Backup,
            GptContainerType.FullDisk,
            resolved.SectorSize,
            sourceLength,
            null,
            Path.GetFullPath(sourcePath),
            File.GetLastWriteTimeUtc(sourcePath),
            protectiveMbr,
            primary,
            backup);
    }

    private static (int SectorSize, FileHeaderCandidate? Primary) ResolvePrimary(
        IReadableBlockDevice stream,
        long sourceLength,
        int? requestedSectorSize)
    {
        if (requestedSectorSize is int explicitSize)
        {
            int validated = GptFormatValidator.ValidateSectorSize(explicitSize);
            return (validated, TryReadPrimary(stream, sourceLength, validated));
        }

        var matches = new List<(int SectorSize, FileHeaderCandidate Candidate)>();
        for (int candidateSize = GptFormatValidator.MinimumSectorSize;
             candidateSize <= GptFormatValidator.MaximumSectorSize;
             candidateSize <<= 1)
        {
            FileHeaderCandidate? candidate = TryReadPrimary(
                stream,
                sourceLength,
                candidateSize);
            if (candidate is not null) matches.Add((candidateSize, candidate));
        }

        if (matches.Count > 1)
            throw new InvalidDataException(Strings.SectorSizeCannotBeInferred);
        return matches.Count == 1
            ? matches[0]
            : (GptFormatValidator.MinimumSectorSize, null);
    }

    private static FileHeaderCandidate? TryReadPrimary(
        IReadableBlockDevice stream,
        long sourceLength,
        int sectorSize)
    {
        if (sourceLength < checked(2L * sectorSize)) return null;
        FileHeaderCandidate? candidate = TryReadCandidate(
            stream,
            sourceLength,
            sectorSize,
            sectorSize);
        return candidate is { Header.CurrentLba: 1 } ? candidate : null;
    }

    private static FileHeaderCandidate? ReadDeclaredBackup(
        IReadableBlockDevice stream,
        long sourceLength,
        int sectorSize,
        FileHeaderCandidate primary)
    {
        if (primary.Header.AlternateLba > long.MaxValue / (ulong)sectorSize)
            return null;
        ulong offset = primary.Header.AlternateLba * (ulong)sectorSize;
        if (offset > (ulong)sourceLength) return null;
        FileHeaderCandidate? candidate = TryReadCandidate(
            stream,
            sourceLength,
            checked((long)offset),
            sectorSize);
        return candidate is not null && IsBackup(candidate) ? candidate : null;
    }

    private static FileHeaderCandidate? FindBackupNearEnd(
        IReadableBlockDevice stream,
        long sourceLength,
        int? requestedSectorSize,
        out (int SectorSize, FileHeaderCandidate? Primary) resolved)
    {
        int tailLength = checked((int)Math.Min(
            sourceLength,
            2L * GptFormatValidator.MaximumSectorSize));
        long tailOffset = sourceLength - tailLength;
        byte[] tail = ReadBytes(stream, tailOffset, tailLength);
        var matches = new List<(int SectorSize, FileHeaderCandidate Candidate)>();
        int searchOffset = 0;
        while (searchOffset <= tail.Length - Signature.Length)
        {
            int relative = tail.AsSpan(searchOffset).IndexOf(Signature);
            if (relative < 0) break;
            int index = checked(searchOffset + relative);
            long absoluteOffset = checked(tailOffset + index);
            if (tail.Length - index >= GptCodec.MinimumHeaderSize)
            {
                try
                {
                    GptHeader header = GptCodec.ReadHeader(tail.AsSpan(index));
                    int inferredSectorSize = ResolveBackupSectorSize(
                        absoluteOffset,
                        header,
                        requestedSectorSize);
                    GptFormatValidator.ValidateHeaderStructure(header, inferredSectorSize);
                    var candidate = new FileHeaderCandidate(absoluteOffset, header);
                    if (IsBackup(candidate)) matches.Add((inferredSectorSize, candidate));
                }
                catch (InvalidDataException)
                {
                }
                catch (OverflowException)
                {
                }
            }

            searchOffset = checked(index + Signature.Length);
        }

        (int SectorSize, FileHeaderCandidate Candidate)[] distinct = matches
            .DistinctBy(static match => (match.SectorSize, match.Candidate.Offset))
            .OrderByDescending(static match => match.Candidate.Offset)
            .ToArray();
        if (distinct.Length == 0)
            throw new InvalidDataException(Strings.SignatureNotFound);
        if (distinct.Select(static match => match.SectorSize).Distinct().Skip(1).Any())
            throw new InvalidDataException(Strings.SectorSizeCannotBeInferred);

        resolved = (distinct[0].SectorSize, null);
        return distinct[0].Candidate;
    }

    private static FileHeaderCandidate? TryFindBackupNearEnd(
        IReadableBlockDevice stream,
        long sourceLength,
        int sectorSize)
    {
        try
        {
            return FindBackupNearEnd(
                stream,
                sourceLength,
                sectorSize,
                out _);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static int ResolveBackupSectorSize(
        long headerOffset,
        GptHeader header,
        int? requestedSectorSize)
    {
        if (requestedSectorSize is int explicitSize)
        {
            int validated = GptFormatValidator.ValidateSectorSize(explicitSize);
            if (header.CurrentLba > long.MaxValue / (ulong)validated ||
                checked((long)header.CurrentLba * validated) != headerOffset)
                throw new InvalidDataException(Strings.HeaderLbaCannotBeMapped);
            return validated;
        }

        if (header.CurrentLba == 0 || (ulong)headerOffset % header.CurrentLba != 0)
            throw new InvalidDataException(Strings.SectorSizeCannotBeInferred);
        ulong inferred = (ulong)headerOffset / header.CurrentLba;
        if (inferred > int.MaxValue)
            throw new InvalidDataException(Strings.SectorSizeCannotBeInferred);
        return GptFormatValidator.ValidateSectorSize((int)inferred);
    }

    private static GptHeaderLayout? TryMapHeader(
        IReadableBlockDevice stream,
        long sourceLength,
        int sectorSize,
        FileHeaderCandidate? candidate,
        GptHeaderCopy copy,
        bool headerOnly)
    {
        if (candidate is null) return null;
        try
        {
            return MapHeader(
                stream,
                sourceLength,
                sectorSize,
                candidate,
                copy,
                headerOnly);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static GptHeaderLayout MapHeader(
        IReadableBlockDevice stream,
        long sourceLength,
        int sectorSize,
        FileHeaderCandidate candidate,
        GptHeaderCopy copy,
        bool headerOnly)
    {
        GptHeader header = candidate.Header;
        GptFormatValidator.ValidateHeaderStructure(header, sectorSize);
        ulong expectedHeaderOffset = checked(header.CurrentLba * (ulong)sectorSize);
        if (expectedHeaderOffset > long.MaxValue ||
            checked((long)expectedHeaderOffset) != candidate.Offset)
            throw new InvalidDataException(Strings.HeaderLbaCannotBeMapped);

        ulong entriesOffsetValue = checked(header.PartitionEntryLba * (ulong)sectorSize);
        if (entriesOffsetValue > long.MaxValue)
            throw new InvalidDataException(Strings.EntryOffsetExceedsSupportedSize);
        long entriesOffset = checked((long)entriesOffsetValue);
        long capacityBytes = copy == GptHeaderCopy.Backup
            ? checked(candidate.Offset - entriesOffset)
            : ResolvePrimaryCapacity(sourceLength, sectorSize, entriesOffset, header);
        if (entriesOffset < 0 || capacityBytes < 0 || entriesOffset > sourceLength ||
            capacityBytes > sourceLength - entriesOffset)
            throw new InvalidDataException(Strings.EntryCapacityNegative);

        int declaredBytes = GptFormatValidator.GetEntryArrayLength(header);
        if (declaredBytes > capacityBytes)
            throw new InvalidDataException(Strings.ImageMissingDeclaredEntryArray);
        int availableEntryCount = checked((int)(
            (ulong)capacityBytes / header.PartitionEntrySize));
        if (availableEntryCount < header.PartitionEntryCount ||
            availableEntryCount > GptFormatValidator.MaximumEntryCount)
            throw new InvalidDataException(Strings.PhysicalEntryCapacityInvalid);

        byte[] headerBytes = ReadBytes(stream, candidate.Offset, sectorSize);
        bool headerCrcValid = GptFormatValidator.IsHeaderCrcValid(headerBytes, header);
        byte[] entryStorage = headerOnly
            ? []
            : ReadBytes(stream, entriesOffset, checked((int)capacityBytes));
        bool? entriesCrcValid = headerOnly
            ? null
            : GptFormatValidator.IsEntryArrayCrcValid(entryStorage, header);
        return new GptHeaderLayout(
            copy,
            candidate.Offset,
            entriesOffset,
            checked((int)capacityBytes),
            availableEntryCount,
            header,
            headerBytes,
            entryStorage,
            new GptCopyStatus(true, headerCrcValid, entriesCrcValid));
    }

    private static long ResolvePrimaryCapacity(
        long sourceLength,
        int sectorSize,
        long entriesOffset,
        GptHeader header)
    {
        long availableBytes = checked(sourceLength - entriesOffset);
        if (header.FirstUsableLba <= header.PartitionEntryLba)
            return availableBytes;
        ulong logicalBytes = checked(
            (header.FirstUsableLba - header.PartitionEntryLba) * (ulong)sectorSize);
        return logicalBytes > long.MaxValue
            ? availableBytes
            : Math.Min(availableBytes, checked((long)logicalBytes));
    }

    private static FileHeaderCandidate? TryReadCandidate(
        IReadableBlockDevice stream,
        long sourceLength,
        long offset,
        int sectorSize)
    {
        if (offset < 0 || offset > sourceLength - sectorSize) return null;
        byte[] sector = ReadBytes(stream, offset, sectorSize);
        if (!sector.AsSpan().StartsWith(Signature)) return null;
        try
        {
            GptHeader header = GptCodec.ReadHeader(sector);
            GptFormatValidator.ValidateHeaderStructure(header, sectorSize);
            return new FileHeaderCandidate(offset, header);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static byte[] ReadBytes(
        IReadableBlockDevice stream,
        long offset,
        int length)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(length);
        BlockDeviceIO.ReadExactlyAt(stream, offset, buffer);
        return buffer;
    }

    private static bool LooksLikeSgdiskContainer(IReadableBlockDevice stream)
    {
        FileHeaderCandidate? primary = TryReadCandidate(
            stream,
            stream.Length,
            SgdiskBlockSize,
            SgdiskBlockSize);
        FileHeaderCandidate? backup = TryReadCandidate(
            stream,
            stream.Length,
            2L * SgdiskBlockSize,
            SgdiskBlockSize);
        return primary is { Header.CurrentLba: 1 } &&
               backup is not null &&
               IsBackup(backup);
    }

    private static bool IsFullDiskContainer(
        IReadableBlockDevice stream,
        GptLayout layout)
    {
        if (layout.BackupHeader is null &&
            (HasNonPhysicalCompactBackup(stream, layout) ||
             HasNonPhysicalBackupNearEnd(stream, layout.SectorSize)))
            return false;

        GptHeaderLayout? geometry = layout.MainHeader ?? layout.BackupHeader;
        if (geometry is null) return false;
        ulong finalLba = Math.Max(
            geometry.Header.CurrentLba,
            geometry.Header.AlternateLba);
        if (finalLba < ulong.MaxValue &&
            finalLba + 1 <= ulong.MaxValue / (ulong)layout.SectorSize &&
            (finalLba + 1) * (ulong)layout.SectorSize <= (ulong)layout.SourceLength)
            return true;

        long compactLength = (layout.MainHeader, layout.BackupHeader) switch
        {
            (not null, not null) => checked(
                (3L * layout.SectorSize) +
                layout.MainHeader.CapacityBytes +
                layout.BackupHeader.CapacityBytes),
            (not null, null) => checked(
                (2L * layout.SectorSize) + layout.MainHeader.CapacityBytes),
            (null, not null) => checked(
                (long)layout.BackupHeader.CapacityBytes + layout.SectorSize),
            _ => layout.SourceLength
        };
        return layout.SourceLength > compactLength;
    }

    private static bool HasNonPhysicalCompactBackup(
        IReadableBlockDevice stream,
        GptLayout layout)
    {
        GptHeaderLayout? primary = layout.MainHeader;
        if (primary is null) return false;
        long offset;
        try
        {
            offset = checked(primary.EntriesOffset + (2L * primary.CapacityBytes));
        }
        catch (OverflowException)
        {
            return false;
        }

        FileHeaderCandidate? candidate = TryReadCandidate(
            stream,
            layout.SourceLength,
            offset,
            layout.SectorSize);
        return candidate is not null &&
               IsBackup(candidate) &&
               !MapsToPhysicalOffset(candidate, layout.SectorSize);
    }

    private static bool HasNonPhysicalBackupNearEnd(
        IReadableBlockDevice stream,
        int sectorSize)
    {
        int tailLength = checked((int)Math.Min(
            stream.Length,
            2L * GptFormatValidator.MaximumSectorSize));
        long tailOffset = stream.Length - tailLength;
        byte[] tail = ReadBytes(stream, tailOffset, tailLength);
        int searchOffset = 0;
        while (searchOffset <= tail.Length - Signature.Length)
        {
            int relative = tail.AsSpan(searchOffset).IndexOf(Signature);
            if (relative < 0) break;
            int index = checked(searchOffset + relative);
            if (tail.Length - index >= GptCodec.MinimumHeaderSize)
            {
                try
                {
                    GptHeader header = GptCodec.ReadHeader(tail.AsSpan(index));
                    GptFormatValidator.ValidateHeaderStructure(header, sectorSize);
                    var candidate = new FileHeaderCandidate(
                        checked(tailOffset + index),
                        header);
                    if (IsBackup(candidate) &&
                        !MapsToPhysicalOffset(candidate, sectorSize))
                        return true;
                }
                catch (InvalidDataException)
                {
                }
                catch (OverflowException)
                {
                }
            }

            searchOffset = checked(index + Signature.Length);
        }

        return false;
    }

    private static bool MapsToPhysicalOffset(
        FileHeaderCandidate candidate,
        int sectorSize) =>
        candidate.Header.CurrentLba <= long.MaxValue / (ulong)sectorSize &&
        checked((long)candidate.Header.CurrentLba * sectorSize) == candidate.Offset;

    private static bool IsBackup(FileHeaderCandidate candidate) =>
        candidate.Header.AlternateLba != 0 &&
        candidate.Header.CurrentLba > candidate.Header.AlternateLba;

    private sealed record FileHeaderCandidate(long Offset, GptHeader Header);
}