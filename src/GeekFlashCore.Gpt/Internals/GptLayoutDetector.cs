namespace GeekFlashCore.Gpt.Internals;

internal static class GptLayoutDetector
{
    private const int SgdiskBlockSize = 512;
    private static ReadOnlySpan<byte> Signature => "EFI PART"u8;

    public static GptLayout Detect(
        ReadOnlySpan<byte> image,
        int? sectorSize,
        bool headerOnly = false)
    {
        if (image.Length < GptCodec.MinimumHeaderSize)
            throw new InvalidDataException(Strings.ImageTooSmallForHeader);

        List<HeaderCandidate> candidates = FindCandidates(image);
        if (candidates.Count == 0)
            throw new InvalidDataException(Strings.SignatureNotFound);

        bool isSgdisk = IsSgdiskLayout(candidates);
        int resolvedSectorSize = sectorSize is int explicitSize
            ? GptFormatValidator.ValidateSectorSize(explicitSize)
            : isSgdisk
                ? InferSgdiskSectorSize(candidates)
                : InferSectorSize(image.Length, candidates);
        HeaderCandidate? mainCandidate = isSgdisk
            ? candidates.First(candidate => candidate.Offset == SgdiskBlockSize)
            : SelectMainCandidate(candidates, resolvedSectorSize);
        HeaderCandidate? backupCandidate = isSgdisk
            ? candidates.First(candidate => candidate.Offset == 2 * SgdiskBlockSize)
            : SelectBackupCandidate(candidates, image.Length, resolvedSectorSize);

        GptHeaderLayout? main = TryMapHeader(
            image,
            resolvedSectorSize,
            mainCandidate,
            GptHeaderCopy.Primary,
            isSgdisk,
            headerOnly);
        GptHeaderLayout? backup = TryMapHeader(
            image,
            resolvedSectorSize,
            backupCandidate,
            GptHeaderCopy.Backup,
            isSgdisk,
            headerOnly);
        if (main is null && backup is null)
            throw new InvalidDataException(Strings.LayoutHasNoHeader);

        GptImageType imageType = isSgdisk
            ? GptImageType.SgdiskBackup
            : main is not null && backup is not null
                ? GptImageType.Both
                : main is not null
                    ? GptImageType.Main
                    : GptImageType.Backup;
        GptContainerType containerType = isSgdisk
            ? GptContainerType.SgdiskBackup
            : IsFullDisk(image.Length, resolvedSectorSize, mainCandidate, backupCandidate) ||
              ExceedsCompactContainerLength(
                  image.Length,
                  resolvedSectorSize,
                  main,
                  backup)
                ? GptContainerType.FullDisk
                : GptContainerType.Compact;
        int mbrLength = isSgdisk ? SgdiskBlockSize : resolvedSectorSize;
        byte[]? protectiveMbr =
            (main is not null || containerType == GptContainerType.FullDisk) &&
            image.Length >= mbrLength
                ? image[..mbrLength].ToArray()
                : null;

        return new GptLayout(
            imageType,
            containerType,
            resolvedSectorSize,
            image.Length,
            containerType == GptContainerType.FullDisk ? image.ToArray() : null,
            null,
            null,
            protectiveMbr,
            main,
            backup);
    }

    private static List<HeaderCandidate> FindCandidates(ReadOnlySpan<byte> image)
    {
        var candidates = new List<HeaderCandidate>(4);
        int searchOffset = 0;
        while (searchOffset <= image.Length - Signature.Length)
        {
            int relativeOffset = image[searchOffset..].IndexOf(Signature);
            if (relativeOffset < 0) break;

            int offset = checked(searchOffset + relativeOffset);
            if (image.Length - offset >= GptCodec.MinimumHeaderSize)
            {
                try
                {
                    GptHeader header = GptCodec.ReadHeader(image[offset..]);
                    GptFormatValidator.ValidateHeaderStructure(header);
                    candidates.Add(new HeaderCandidate(offset, header));
                }
                catch (InvalidDataException)
                {
                }
            }

            searchOffset = checked(offset + Signature.Length);
        }

        return candidates;
    }

    private static int InferSectorSize(
        int imageLength,
        IReadOnlyList<HeaderCandidate> candidates)
    {
        foreach (HeaderCandidate candidate in candidates.OrderBy(static item => item.Offset))
        {
            if (candidate.Header.CurrentLba == 0 ||
                (ulong)candidate.Offset % candidate.Header.CurrentLba != 0)
                continue;

            ulong inferred = (ulong)candidate.Offset / candidate.Header.CurrentLba;
            if (inferred <= int.MaxValue && IsValidSectorSize((int)inferred))
                return (int)inferred;
        }

        foreach (HeaderCandidate candidate in candidates)
        {
            int inferred = checked(imageLength - candidate.Offset);
            if (IsValidSectorSize(inferred)) return inferred;
        }

        throw new InvalidDataException(Strings.SectorSizeCannotBeInferred);
    }

    private static int InferSgdiskSectorSize(
        IReadOnlyList<HeaderCandidate> candidates)
    {
        HeaderCandidate primary = candidates.First(candidate =>
            candidate.Offset == SgdiskBlockSize && IsMain(candidate));
        HeaderCandidate backup = candidates.First(candidate =>
            candidate.Offset == 2 * SgdiskBlockSize && IsBackup(candidate));
        ulong declaredBytes = checked(
            (ulong)primary.Header.PartitionEntryCount *
            primary.Header.PartitionEntrySize);
        ulong primaryGap = primary.Header.FirstUsableLba >
                           primary.Header.PartitionEntryLba
            ? primary.Header.FirstUsableLba - primary.Header.PartitionEntryLba
            : 0;
        ulong backupGap = backup.Header.CurrentLba > backup.Header.PartitionEntryLba
            ? backup.Header.CurrentLba - backup.Header.PartitionEntryLba
            : 0;

        var matches = new List<int>();
        for (int candidate = GptFormatValidator.MinimumSectorSize;
             candidate <= GptFormatValidator.MaximumSectorSize;
             candidate <<= 1)
        {
            ulong declaredSectors = GptFormatValidator.GetSectorCountForBytes(
                declaredBytes,
                candidate);
            if ((primaryGap == 0 || primaryGap == declaredSectors) &&
                (backupGap == 0 || backupGap == declaredSectors))
                matches.Add(candidate);
        }

        return matches.Count == 1
            ? matches[0]
            : throw new InvalidDataException(Strings.SectorSizeCannotBeInferred);
    }

    private static HeaderCandidate? SelectMainCandidate(
        IEnumerable<HeaderCandidate> candidates,
        int sectorSize) =>
        candidates
            .Where(IsMain)
            .Where(candidate => candidate.Offset % sectorSize == 0)
            .OrderByDescending(candidate => candidate.Offset == sectorSize)
            .ThenBy(candidate => candidate.Offset)
            .FirstOrDefault();

    private static HeaderCandidate? SelectBackupCandidate(
        IEnumerable<HeaderCandidate> candidates,
        int imageLength,
        int sectorSize) =>
        candidates
            .Where(IsBackup)
            .Where(candidate => candidate.Offset % sectorSize == 0)
            .OrderByDescending(candidate => candidate.Offset + sectorSize == imageLength)
            .ThenByDescending(candidate => candidate.Offset)
            .FirstOrDefault();

    private static GptHeaderLayout? TryMapHeader(
        ReadOnlySpan<byte> image,
        int sectorSize,
        HeaderCandidate? candidate,
        GptHeaderCopy copy,
        bool isSgdisk,
        bool headerOnly)
    {
        if (candidate is null) return null;
        try
        {
            return MapHeader(image, sectorSize, candidate, copy, isSgdisk, headerOnly);
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
        ReadOnlySpan<byte> image,
        int sectorSize,
        HeaderCandidate candidate,
        GptHeaderCopy copy,
        bool isSgdisk,
        bool headerOnly)
    {
        GptHeader header = candidate.Header;
        int headerStorageSize = isSgdisk ? SgdiskBlockSize : sectorSize;
        GptFormatValidator.ValidateHeaderStructure(header, headerStorageSize);
        if (candidate.Offset > image.Length - headerStorageSize)
            throw new InvalidDataException(Strings.ImageTooSmallForHeader);

        int entriesOffset;
        long capacityBytes;
        if (isSgdisk)
        {
            entriesOffset = 3 * SgdiskBlockSize;
            capacityBytes = image.Length - entriesOffset;
        }
        else
        {
            bool unpatchedBackup = copy == GptHeaderCopy.Backup &&
                                   header.CurrentLba == 0 &&
                                   header.AlternateLba == 1 &&
                                   header.PartitionEntryLba == 0;
            if (unpatchedBackup)
            {
                entriesOffset = 0;
                capacityBytes = candidate.Offset - entriesOffset;
            }
            else
            {
                ulong headerFileSector = (ulong)(candidate.Offset / sectorSize);
                if (header.CurrentLba < headerFileSector)
                    throw new InvalidDataException(Strings.HeaderLbaCannotBeMapped);
                ulong fragmentBaseLba = header.CurrentLba - headerFileSector;
                if (header.PartitionEntryLba < fragmentBaseLba)
                    throw new InvalidDataException(Strings.EntryLbaPrecedesFragment);

                ulong entryFileSector = header.PartitionEntryLba - fragmentBaseLba;
                ulong entriesOffset64 = checked(entryFileSector * (ulong)sectorSize);
                if (entriesOffset64 > int.MaxValue)
                    throw new InvalidDataException(Strings.EntryOffsetExceedsSupportedSize);
                entriesOffset = (int)entriesOffset64;

                if (copy == GptHeaderCopy.Backup)
                {
                    capacityBytes = candidate.Offset - entriesOffset;
                }
                else
                {
                    capacityBytes = image.Length - entriesOffset;
                    if (header.FirstUsableLba > header.PartitionEntryLba)
                    {
                        ulong logicalBytes = checked(
                            (header.FirstUsableLba - header.PartitionEntryLba) *
                            (ulong)sectorSize);
                        capacityBytes = (long)Math.Min((ulong)capacityBytes, logicalBytes);
                    }
                }
            }
        }

        if (entriesOffset < 0 || capacityBytes < 0 ||
            entriesOffset > image.Length || capacityBytes > image.Length - entriesOffset)
            throw new InvalidDataException(Strings.EntryCapacityNegative);
        int declaredBytes = GptFormatValidator.GetEntryArrayLength(header);
        if (declaredBytes > capacityBytes)
            throw new InvalidDataException(Strings.ImageMissingDeclaredEntryArray);
        int availableEntryCount = checked((int)((ulong)capacityBytes / header.PartitionEntrySize));
        if (availableEntryCount < header.PartitionEntryCount ||
            availableEntryCount > GptFormatValidator.MaximumEntryCount)
            throw new InvalidDataException(Strings.PhysicalEntryCapacityInvalid);

        byte[] headerBytes = image.Slice(candidate.Offset, headerStorageSize).ToArray();
        bool headerCrcValid = GptFormatValidator.IsHeaderCrcValid(headerBytes, header);
        byte[] entryStorage = headerOnly
            ? []
            : image.Slice(entriesOffset, checked((int)capacityBytes)).ToArray();
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

    private static bool IsSgdiskLayout(IReadOnlyList<HeaderCandidate> candidates) =>
        candidates.Any(candidate => candidate.Offset == SgdiskBlockSize && IsMain(candidate)) &&
        candidates.Any(candidate => candidate.Offset == 2 * SgdiskBlockSize && IsBackup(candidate));

    private static bool IsFullDisk(
        int imageLength,
        int sectorSize,
        HeaderCandidate? main,
        HeaderCandidate? backup)
    {
        HeaderCandidate? geometry = main ?? backup;
        if (geometry is null) return false;
        ulong finalLba = Math.Max(
            geometry.Header.CurrentLba,
            geometry.Header.AlternateLba);
        if (finalLba <= 1) return false;
        if (finalLba + 1 > ulong.MaxValue / (ulong)sectorSize) return false;
        ulong expectedLength = checked((finalLba + 1) * (ulong)sectorSize);
        if (expectedLength > (ulong)imageLength) return false;

        bool mainAtLogicalLba = main is null ||
                                (ulong)main.Offset == main.Header.CurrentLba * (ulong)sectorSize;
        bool backupAtLogicalLba = backup is null ||
                                  (ulong)backup.Offset == backup.Header.CurrentLba * (ulong)sectorSize;
        return mainAtLogicalLba && backupAtLogicalLba;
    }

    private static bool ExceedsCompactContainerLength(
        int imageLength,
        int sectorSize,
        GptHeaderLayout? main,
        GptHeaderLayout? backup)
    {
        long compactLength = (main, backup) switch
        {
            (not null, not null) => checked(
                (3L * sectorSize) + main.CapacityBytes + backup.CapacityBytes),
            (not null, null) => checked((2L * sectorSize) + main.CapacityBytes),
            (null, not null) => checked((long)backup.CapacityBytes + sectorSize),
            _ => imageLength
        };
        return imageLength > compactLength;
    }

    private static bool IsMain(HeaderCandidate candidate) =>
        candidate.Header.CurrentLba != 0 &&
        (candidate.Header.AlternateLba == 0 ||
         candidate.Header.CurrentLba < candidate.Header.AlternateLba);

    private static bool IsBackup(HeaderCandidate candidate) =>
        candidate.Header.AlternateLba != 0 &&
        (candidate.Header.CurrentLba == 0 ||
         candidate.Header.CurrentLba > candidate.Header.AlternateLba);

    private static bool IsValidSectorSize(int sectorSize) =>
        sectorSize is >= GptFormatValidator.MinimumSectorSize and
            <= GptFormatValidator.MaximumSectorSize &&
        (sectorSize & (sectorSize - 1)) == 0;

    private sealed record HeaderCandidate(int Offset, GptHeader Header);
}