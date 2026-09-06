using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.ImageFormats.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal static class LpLayoutPlanner
{
    internal static LpPlanResult CreatePlan(
        LpEditSession session,
        LpDraftSnapshot draft,
        LpPlanOptions options,
        CancellationToken cancellationToken,
        out LpPlanState? planState)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(options);
        planState = null;

        LpSessionSnapshot snapshot = session.Snapshot;
        for (int slot = 0; slot < snapshot.Slots.Length; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot != session.SlotNumber && !snapshot.Slots[slot].Any(copy => copy.IsValid))
            {
                return Failure(
                    LpEditErrorCode.UnreadableNonTargetSlot,
                    "UnreadableNonTargetSlot",
                    [slot],
                    new LpDiagnosticContext(slotNumber: slot));
            }
        }

        LpBlockDevice[] blockDevices = snapshot.Baseline.BlockDevices;
        if (blockDevices.Length == 0 ||
            snapshot.MetadataDevice.Length < 0 ||
            (ulong)snapshot.MetadataDevice.Length < blockDevices[0].Size)
        {
            return Failure(LpEditErrorCode.SourceChanged, "SourceChanged");
        }

        var allowedDevices = snapshot.ResolvedDevices.Keys.ToHashSet();
        List<LpSectorRange> allOccupied = GetPhysicalRanges(
            snapshot.Slots.SelectMany(slot => slot).Where(copy => copy.IsValid).Select(copy => copy.Model!));
        List<LpSectorRange> nonTargetOccupied = GetPhysicalRanges(
            snapshot.Slots
                .Where((_, slot) => slot != session.SlotNumber)
                .SelectMany(slot => slot)
                .Where(copy => copy.IsValid)
                .Select(copy => copy.Model!));
        List<LpSectorRange> strictFree = LpIntervalMath.CreateFreeRanges(
            blockDevices,
            allowedDevices,
            allOccupied);

        Dictionary<LpPartitionHandle, LpExtent[]> retained = BuildRetainedExtents(draft);
        List<LpSectorRange> retainedRanges = GetPhysicalRanges(retained.Values);
        List<LpSectorRange> originalRanges = GetPhysicalRanges(
            draft.OriginalPartitions.Select(partition => partition.OriginalExtents));
        List<LpSectorRange> inPlaceEligible = LpIntervalMath.Subtract(
            LpIntervalMath.Subtract(originalRanges, nonTargetOccupied),
            retainedRanges)
            .Where(range => allowedDevices.Contains(range.TargetSource))
            .ToList();

        BuildAttempt? attempt = TryBuild(
            session,
            draft,
            strictFree,
            retained,
            useInPlace: false,
            inPlaceEligible,
            cancellationToken);
        if (attempt is null)
        {
            BuildAttempt? inPlaceAttempt = TryBuild(
                session,
                draft,
                strictFree,
                retained,
                useInPlace: true,
                inPlaceEligible,
                cancellationToken);
            if (inPlaceAttempt is null)
            {
                return Failure(LpEditErrorCode.InsufficientSpace, "InsufficientSpace");
            }
            if (!options.AllowInPlaceDataOverwrite)
            {
                return Failure(
                    LpEditErrorCode.InPlaceOverwriteRequired,
                    "InPlaceOverwriteRequired");
            }
            attempt = inPlaceAttempt;
        }

        LpPlanResult? validationFailure = ValidateGroupsAndExtents(
            session,
            draft,
            attempt,
            cancellationToken);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        LpFinalModel finalModel;
        try
        {
            finalModel = BuildFinalModel(session, draft, attempt);
        }
        catch (OverflowException)
        {
            return Failure(LpEditErrorCode.MetadataTooLarge, "MetadataTooLarge");
        }

        foreach (LpExtent extent in finalModel.Extents)
        {
            if (extent.TargetType == LpExtentTargetType.Linear &&
                !snapshot.ResolvedDevices.ContainsKey(extent.TargetSource))
            {
                return Failure(
                    LpEditErrorCode.UnsupportedMetadataFeature,
                    "MultiDeviceResolverRequired",
                    [extent.TargetSource],
                    new LpDiagnosticContext(blockDeviceIndex: extent.TargetSource));
            }
        }

        byte[] newMetadata;
        byte[] baselineMetadata;
        try
        {
            newMetadata = LpMetadataEncoder.EncodeAndValidate(
                snapshot.Geometry,
                session.SlotNumber,
                finalModel,
                session.ReadLimits);
            baselineMetadata = LpMetadataEncoder.EncodeAndValidate(
                snapshot.Geometry,
                session.SlotNumber,
                ToFinalModel(snapshot.Baseline),
                session.ReadLimits);
        }
        catch (Exception exception) when (
            exception is OverflowException or InvalidOperationException or ImageFormatException)
        {
            return Failure(
                LpEditErrorCode.MetadataTooLarge,
                "MetadataTooLarge",
                context: new LpDiagnosticContext(slotNumber: session.SlotNumber));
        }

        List<LpSectorRange> finalRanges = GetPhysicalRanges(new[] { finalModel });
        List<LpSectorRange> oldTargetRanges = GetPhysicalRanges(
            snapshot.Slots[session.SlotNumber]
                .Where(copy => copy.IsValid)
                .Select(copy => copy.Model!));
        List<LpSectorRange> freed = LpIntervalMath.Subtract(
            oldTargetRanges,
            finalRanges.Concat(nonTargetOccupied));
        List<LpSectorRange> postCommitOccupied = LpIntervalMath.Normalize(
            finalRanges.Concat(nonTargetOccupied));
        List<LpSectorRange> postCommitFree = LpIntervalMath.CreateFreeRanges(
            blockDevices,
            allowedDevices,
            postCommitOccupied);

        DataRollbackState rollbackState = attempt.UsesInPlace
            ? DataRollbackState.Unavailable
            : DataRollbackState.OriginalDataPreserved;
        LpCommitPlan publicPlan;
        try
        {
            publicPlan = BuildPublicPlan(
                session,
                draft,
                attempt,
                finalModel,
                blockDevices,
                allowedDevices,
                postCommitFree,
                rollbackState);
        }
        catch (OverflowException)
        {
            return Failure(
                LpEditErrorCode.UnsupportedMetadataFeature,
                "CapacityExceedsSupportedRange");
        }
        planState = new LpPlanState
        {
            PublicPlan = publicPlan,
            FinalModel = finalModel,
            NewMetadata = newMetadata,
            BaselineMetadata = baselineMetadata,
            DataWrites = attempt.DataWrites.ToArray(),
            FreedRanges = freed.ToArray(),
            PartitionIndices = draft.Partitions
                .Select((partition, index) => (partition.Handle, index))
                .ToDictionary(pair => pair.Handle, pair => pair.index),
            ChangedPartitions = GetChangedPartitions(draft).ToArray(),
        };
        return new LpPlanSuccess(publicPlan);
    }

    private static LpCommitPlan BuildPublicPlan(
        LpEditSession session,
        LpDraftSnapshot draft,
        BuildAttempt attempt,
        LpFinalModel finalModel,
        IReadOnlyList<LpBlockDevice> blockDevices,
        HashSet<uint> allowedDevices,
        IReadOnlyList<LpSectorRange> postCommitFree,
        DataRollbackState rollbackState)
    {
        long requiredLogicalBytes = 0;
        foreach (LpPartitionState partition in draft.Partitions)
        {
            requiredLogicalBytes = checked(requiredLogicalBytes + partition.RequestedSize);
        }

        long allocatedPhysicalBytes = checked(
            (long)SumSectors(finalModel.Extents.Where(
                extent => extent.TargetType == LpExtentTargetType.Linear)) *
            LpFormat.SectorSize);
        long allocatableBytes = checked((long)SumSectors(
            blockDevices.Select((device, index) =>
            {
                uint source = checked((uint)index);
                ulong end = device.Size / LpFormat.SectorSize;
                return allowedDevices.Contains(source) && end > device.FirstLogicalSector
                    ? new LpSectorRange(
                        source,
                        device.FirstLogicalSector,
                        end - device.FirstLogicalSector)
                    : default;
            })) * LpFormat.SectorSize);
        long remainingFreeBytes = checked(
            (long)SumSectors(postCommitFree) * LpFormat.SectorSize);

        var usedByGroup = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (LpGroupState group in draft.Groups)
        {
            usedByGroup.Add(group.RawName, 0);
        }
        foreach (LpPartitionState partition in draft.Partitions)
        {
            usedByGroup[partition.GroupRawName] = checked(
                usedByGroup[partition.GroupRawName] +
                checked((ulong)partition.RequestedSize));
        }

        var groupUsages = new LpGroupUsage[draft.Groups.Length];
        for (int index = 0; index < draft.Groups.Length; index++)
        {
            LpGroupState group = draft.Groups[index];
            groupUsages[index] = new LpGroupUsage(
                group.RawName,
                LpNameValidator.GetEffectiveName(
                    group.RawName,
                    (group.Flags & LpGroupFlags.SlotSuffixed) != 0,
                    session.SlotNumber),
                group.MaximumSize,
                usedByGroup[group.RawName]);
        }

        LpPlannedDataWrite[] publicWrites = attempt.DataWrites
            .Select(write => new LpPlannedDataWrite(
                write.Partition,
                write.PartitionName,
                write.Kind == LpDataWriteKind.Image
                    ? LpPlannedWriteKind.Image
                    : LpPlannedWriteKind.Zero,
                write.SourceOffset,
                write.TargetSource,
                write.TargetOffset,
                write.Length))
            .ToArray();
        return new LpCommitPlan(
            session.SessionId,
            session.SlotNumber,
            rollbackState,
            requiredLogicalBytes,
            allocatedPhysicalBytes,
            allocatableBytes,
            remainingFreeBytes,
            attempt.UsesInPlace,
            finalModel.Partitions,
            finalModel.Extents,
            finalModel.Groups,
            finalModel.BlockDevices,
            publicWrites,
            groupUsages);
    }

    private static BuildAttempt? TryBuild(
        LpEditSession session,
        LpDraftSnapshot draft,
        List<LpSectorRange> strictFree,
        Dictionary<LpPartitionHandle, LpExtent[]> retained,
        bool useInPlace,
        List<LpSectorRange> inPlaceEligible,
        CancellationToken cancellationToken)
    {
        var allocator = new LpSectorAllocator(
            session.Snapshot.Baseline.BlockDevices,
            session.Geometry.LogicalBlockSize,
            strictFree);
        if (useInPlace)
        {
            allocator.AddRanges(inPlaceEligible);
        }

        var extents = new Dictionary<LpPartitionHandle, LpExtent[]>();
        var writes = new List<LpDataWrite>();
        bool usedInPlace = false;
        foreach (LpPartitionState partition in draft.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LpExtent[] kept = retained[partition.Handle];
            ulong requiredSectors = checked((ulong)(partition.RequestedSize / LpFormat.SectorSize));
            ulong keptSectors = SumSectors(kept);
            ulong remaining = checked(requiredSectors - keptSectors);
            var allocated = new List<LpExtent>();

            if (remaining != 0 && kept.Length != 0)
            {
                LpExtent last = kept[^1];
                if (last.TargetType == LpExtentTargetType.Linear &&
                    allocator.TryAllocateAt(
                        last.TargetSource,
                        checked(last.TargetData + last.SectorCount),
                        remaining,
                        out LpExtent contiguous))
                {
                    allocated.Add(contiguous);
                    remaining -= contiguous.SectorCount;
                }
            }
            if (remaining != 0)
            {
                if (!allocator.TryAllocate(remaining, out LpExtent[] additional))
                {
                    return null;
                }
                allocated.AddRange(additional);
            }

            LpExtent[] finalExtents = MergeExtents(kept, allocated);
            extents[partition.Handle] = finalExtents;
            if (useInPlace && allocated.Any(extent =>
                inPlaceEligible.Any(range => Overlaps(extent, range))))
            {
                usedInPlace = true;
            }
            AddDataWrites(session, partition, allocated, finalExtents, writes);
        }

        return new BuildAttempt(extents, writes, usedInPlace);
    }

    private static Dictionary<LpPartitionHandle, LpExtent[]> BuildRetainedExtents(
        LpDraftSnapshot draft)
    {
        var result = new Dictionary<LpPartitionHandle, LpExtent[]>();
        foreach (LpPartitionState partition in draft.Partitions)
        {
            if (partition.ReplacementImage is not null || partition.IsNew)
            {
                result[partition.Handle] = [];
                continue;
            }

            ulong requestedSectors = checked((ulong)(partition.RequestedSize / LpFormat.SectorSize));
            result[partition.Handle] = Truncate(partition.OriginalExtents, requestedSectors);
        }
        return result;
    }

    private static LpExtent[] Truncate(LpExtent[] source, ulong sectors)
    {
        if (sectors == 0)
        {
            return [];
        }

        var result = new List<LpExtent>();
        ulong remaining = sectors;
        foreach (LpExtent extent in source)
        {
            if (remaining == 0)
            {
                break;
            }
            ulong take = Math.Min(remaining, extent.SectorCount);
            result.Add(extent with { SectorCount = take });
            remaining -= take;
        }
        return result.ToArray();
    }

    private static void AddDataWrites(
        LpEditSession session,
        LpPartitionState partition,
        List<LpExtent> allocated,
        LpExtent[] finalExtents,
        List<LpDataWrite> writes)
    {
        string name = LpNameValidator.GetEffectiveName(
            partition.RawName,
            (partition.Attributes & LpPartitionAttributes.SlotSuffixed) != 0,
            session.SlotNumber);
        if (partition.ReplacementImage is not null)
        {
            long logicalOffset = 0;
            foreach (LpExtent extent in finalExtents)
            {
                long extentBytes = checked((long)extent.SectorCount * LpFormat.SectorSize);
                if (extent.TargetType != LpExtentTargetType.Linear)
                {
                    throw new InvalidDataException(Resources.SourceChanged);
                }
                long imageBytes = logicalOffset < partition.ReplacementImage.LogicalLength
                    ? Math.Min(extentBytes, partition.ReplacementImage.LogicalLength - logicalOffset)
                    : 0;
                if (imageBytes != 0)
                {
                    writes.Add(new LpDataWrite(
                        partition.Handle,
                        name,
                        LpDataWriteKind.Image,
                        partition.ReplacementImage,
                        logicalOffset,
                        extent.TargetSource,
                        checked((long)extent.TargetData * LpFormat.SectorSize),
                        imageBytes));
                }
                if (imageBytes < extentBytes)
                {
                    writes.Add(new LpDataWrite(
                        partition.Handle,
                        name,
                        LpDataWriteKind.Zero,
                        Image: null,
                        SourceOffset: 0,
                        extent.TargetSource,
                        checked((long)extent.TargetData * LpFormat.SectorSize + imageBytes),
                        extentBytes - imageBytes));
                }
                logicalOffset = checked(logicalOffset + extentBytes);
            }
            return;
        }

        if (partition.IsNew || allocated.Count != 0)
        {
            foreach (LpExtent extent in partition.IsNew ? finalExtents : allocated.ToArray())
            {
                writes.Add(new LpDataWrite(
                    partition.Handle,
                    name,
                    LpDataWriteKind.Zero,
                    Image: null,
                    SourceOffset: 0,
                    extent.TargetSource,
                    checked((long)extent.TargetData * LpFormat.SectorSize),
                    checked((long)extent.SectorCount * LpFormat.SectorSize)));
            }
        }
    }

    private static LpExtent[] MergeExtents(
        IEnumerable<LpExtent> retained,
        IEnumerable<LpExtent> allocated)
    {
        var result = new List<LpExtent>();
        foreach (LpExtent extent in retained.Concat(allocated))
        {
            if (extent.SectorCount == 0)
            {
                continue;
            }
            if (result.Count != 0)
            {
                LpExtent previous = result[^1];
                if (previous.TargetType == LpExtentTargetType.Linear &&
                    extent.TargetType == LpExtentTargetType.Linear &&
                    previous.TargetSource == extent.TargetSource &&
                    checked(previous.TargetData + previous.SectorCount) == extent.TargetData)
                {
                    result[^1] = previous with
                    {
                        SectorCount = checked(previous.SectorCount + extent.SectorCount)
                    };
                    continue;
                }
            }
            result.Add(extent);
        }
        return result.ToArray();
    }

    private static LpPlanResult? ValidateGroupsAndExtents(
        LpEditSession session,
        LpDraftSnapshot draft,
        BuildAttempt attempt,
        CancellationToken cancellationToken)
    {
        foreach (LpGroupState group in draft.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.MaximumSize == 0)
            {
                continue;
            }
            ulong used = 0;
            foreach (LpPartitionState partition in draft.Partitions)
            {
                if (string.Equals(partition.GroupRawName, group.RawName, StringComparison.Ordinal))
                {
                    used = checked(used + (ulong)partition.RequestedSize);
                }
            }
            if (used > group.MaximumSize)
            {
                return Failure(
                    LpEditErrorCode.GroupCapacityExceeded,
                    "GroupCapacityExceeded",
                    [group.RawName, group.MaximumSize, used],
                    new LpDiagnosticContext(groupName: group.RawName));
            }
        }

        var physical = new List<LpSectorRange>();
        foreach (LpExtent[] extents in attempt.Extents.Values)
        {
            foreach (LpExtent extent in extents)
            {
                if (extent.TargetType != LpExtentTargetType.Linear)
                {
                    continue;
                }
                var range = new LpSectorRange(
                    extent.TargetSource,
                    extent.TargetData,
                    extent.SectorCount);
                physical.Add(range);
            }
        }
        physical.Sort(static (left, right) =>
        {
            int sourceComparison = left.TargetSource.CompareTo(right.TargetSource);
            return sourceComparison != 0
                ? sourceComparison
                : left.StartSector.CompareTo(right.StartSector);
        });
        for (int index = 1; index < physical.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LpSectorRange previous = physical[index - 1];
            LpSectorRange current = physical[index];
            if (previous.TargetSource == current.TargetSource &&
                current.StartSector < previous.EndSector)
            {
                return Failure(LpEditErrorCode.ExtentConflict, "ExtentConflict");
            }
        }
        return null;
    }

    private static LpFinalModel BuildFinalModel(
        LpEditSession session,
        LpDraftSnapshot draft,
        BuildAttempt attempt)
    {
        var groups = new LpPartitionGroup[draft.Groups.Length];
        var groupIndices = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int index = 0; index < groups.Length; index++)
        {
            LpGroupState group = draft.Groups[index];
            groupIndices.Add(group.RawName, checked((uint)index));
            groups[index] = new LpPartitionGroup(
                group.RawName,
                LpNameValidator.GetEffectiveName(
                    group.RawName,
                    (group.Flags & LpGroupFlags.SlotSuffixed) != 0,
                    session.SlotNumber),
                group.Flags,
                group.MaximumSize);
        }

        var partitions = new LpPartition[draft.Partitions.Length];
        var extents = new List<LpExtent>();
        for (int index = 0; index < partitions.Length; index++)
        {
            LpPartitionState state = draft.Partitions[index];
            LpExtent[] partitionExtents = attempt.Extents[state.Handle];
            uint firstExtent = checked((uint)extents.Count);
            extents.AddRange(partitionExtents);
            partitions[index] = new LpPartition(
                state.RawName,
                LpNameValidator.GetEffectiveName(
                    state.RawName,
                    (state.Attributes & LpPartitionAttributes.SlotSuffixed) != 0,
                    session.SlotNumber),
                state.Attributes,
                firstExtent,
                checked((uint)partitionExtents.Length),
                groupIndices[state.GroupRawName],
                state.RequestedSize);
        }

        return new LpFinalModel(
            session.Snapshot.Baseline.Header,
            partitions,
            extents.ToArray(),
            groups,
            session.Snapshot.Baseline.BlockDevices.ToArray());
    }

    private static LpFinalModel ToFinalModel(LpTableModel model) => new(
        model.Header,
        model.Partitions.ToArray(),
        model.Extents.ToArray(),
        model.Groups.ToArray(),
        model.BlockDevices.ToArray());

    private static IEnumerable<LpPartitionHandle> GetChangedPartitions(LpDraftSnapshot draft)
    {
        var original = draft.OriginalPartitions.ToDictionary(partition => partition.Handle);
        foreach (LpPartitionState partition in draft.Partitions)
        {
            if (!original.TryGetValue(partition.Handle, out LpPartitionState? previous) ||
                partition.RawName != previous.RawName ||
                partition.Attributes != previous.Attributes ||
                partition.GroupRawName != previous.GroupRawName ||
                partition.RequestedSize != previous.RequestedSize ||
                partition.ReplacementImage is not null)
            {
                yield return partition.Handle;
            }
        }
    }

    private static List<LpSectorRange> GetPhysicalRanges(IEnumerable<LpTableModel> models) =>
        GetPhysicalRanges(models.Select(model => model.Extents));

    private static List<LpSectorRange> GetPhysicalRanges(IEnumerable<LpFinalModel> models) =>
        GetPhysicalRanges(models.Select(model => model.Extents));

    private static List<LpSectorRange> GetPhysicalRanges(IEnumerable<LpExtent[]> extentSets)
    {
        var result = new List<LpSectorRange>();
        foreach (LpExtent[] extents in extentSets)
        {
            foreach (LpExtent extent in extents)
            {
                if (extent.TargetType == LpExtentTargetType.Linear)
                {
                    result.Add(new LpSectorRange(
                        extent.TargetSource,
                        extent.TargetData,
                        extent.SectorCount));
                }
            }
        }
        return LpIntervalMath.Normalize(result);
    }

    private static ulong SumSectors(IEnumerable<LpExtent> extents)
    {
        ulong total = 0;
        foreach (LpExtent extent in extents)
        {
            total = checked(total + extent.SectorCount);
        }
        return total;
    }

    private static ulong SumSectors(IEnumerable<LpSectorRange> ranges)
    {
        ulong total = 0;
        foreach (LpSectorRange range in ranges)
        {
            total = checked(total + range.SectorCount);
        }
        return total;
    }

    private static bool Overlaps(LpExtent extent, LpSectorRange range) =>
        extent.TargetType == LpExtentTargetType.Linear &&
        LpIntervalMath.Overlaps(
            new LpSectorRange(extent.TargetSource, extent.TargetData, extent.SectorCount),
            range);

    private static LpPlanFailure Failure(
        LpEditErrorCode code,
        string resourceKey,
        IReadOnlyList<object?>? arguments = null,
        LpDiagnosticContext? context = null) =>
        new(new LpEditFailure(code, $"Lp.Edit.{resourceKey}", arguments, context));

    private sealed record BuildAttempt(
        Dictionary<LpPartitionHandle, LpExtent[]> Extents,
        List<LpDataWrite> DataWrites,
        bool UsesInPlace);
}
