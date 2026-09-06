using System.Buffers;
using System.Security.Cryptography;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using Microsoft.Extensions.Logging;

namespace GeekFlashCore.Android.Lp;

internal sealed class LpCommitter
{
    private LpCommitter()
    {
    }

    internal static async ValueTask<LpCommitResult> CommitAsync(
        LpEditSession session,
        LpPlanState plan,
        ILpWritableBlockDeviceResolver resolver,
        LpCommitOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (options.VerifyPayloadHash && !options.VerifyAfterCommit)
        {
            throw Failure(
                LpCommitErrorCode.InvalidVerificationOptions,
                LpCommitPhase.NotStarted,
                metadataChanged: false,
                DataRollbackState.NotRequired,
                MetadataCopyState.Unknown,
                MetadataCopyState.Unknown,
                DurabilityLevel.None);
        }

        cancellationToken.ThrowIfCancellationRequested();
        LpPartitionImageSource[] images = GetDistinctImages(plan.DataWrites);
        foreach (LpPartitionImageSource image in images)
        {
            try
            {
                await image.ValidateAsync(
                        session.WriteLimits.DataBufferSize,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.DataWriteFailed,
                    LpCommitPhase.NotStarted,
                    metadataChanged: false,
                    DataRollbackState.NotRequired,
                    MetadataCopyState.Unknown,
                    MetadataCopyState.Unknown,
                    DurabilityLevel.None,
                    exception);
            }
        }

        LpLog.CommitStarted(logger, session.SlotNumber);
        var leases = new List<(uint Index, IWritableBlockDeviceLease Lease)>();
        var devices = new Dictionary<uint, IWritableBlockDevice>();
        LpCommitPhase phase = LpCommitPhase.NotStarted;
        MetadataCopyState primaryState = session.Snapshot.Slots[session.SlotNumber][0].IsValid
            ? MetadataCopyState.OriginalValid
            : MetadataCopyState.OriginalInvalid;
        MetadataCopyState backupState = session.Snapshot.Slots[session.SlotNumber][1].IsValid
            ? MetadataCopyState.OriginalValid
            : MetadataCopyState.OriginalInvalid;
        DurabilityLevel durability = DurabilityLevel.None;
        bool dataAccepted = false;
        bool metadataChanged = false;

        try
        {
            SortedSet<uint> requiredIndices = GetRequiredDeviceIndices(plan, options);
            var resolvedIds = new HashSet<BlockDeviceId>();
            try
            {
                foreach (uint index in requiredIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LpBlockDevice descriptor = plan.FinalModel.BlockDevices[checked((int)index)];
                    IWritableBlockDeviceLease lease = await resolver
                        .ResolveAsync(descriptor, cancellationToken)
                        .ConfigureAwait(false) ?? throw new InvalidOperationException(
                            Resources.ResolverReturnedNull);
                    leases.Add((index, lease));
                    IWritableBlockDevice device = lease.Device;
                    if (!session.Snapshot.ResolvedDevices.TryGetValue(index, out LpDeviceIdentity expected) ||
                        device.Id != expected.Id ||
                        device.Length != expected.Length ||
                        device.LogicalBlockSize != expected.LogicalBlockSize ||
                        device.Length < 0 ||
                        (ulong)device.Length < descriptor.Size)
                    {
                        throw Failure(
                            LpCommitErrorCode.DeviceIdentityMismatch,
                            phase,
                            metadataChanged,
                            DataRollbackState.NotRequired,
                            primaryState,
                            backupState,
                            durability);
                    }
                    if (!resolvedIds.Add(device.Id))
                    {
                        throw Failure(
                            LpCommitErrorCode.AliasedBlockDevice,
                            phase,
                            metadataChanged,
                            DataRollbackState.NotRequired,
                            primaryState,
                            backupState,
                            durability);
                    }
                    devices.Add(index, device);
                }
            }
            catch (LpCommitException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.ResolverFailed,
                    phase,
                    metadataChanged,
                    DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            phase = LpCommitPhase.DevicesResolved;

            IWritableBlockDevice metadataDevice = devices[0];
            bool unchanged;
            try
            {
                unchanged = IsSnapshotUnchanged(metadataDevice, session, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.MetadataReadFailed,
                    phase,
                    metadataChanged,
                    DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            if (!unchanged)
            {
                throw Failure(
                    LpCommitErrorCode.SourceChangedBeforeWrite,
                    phase,
                    metadataChanged,
                    DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability);
            }

            Dictionary<LpPartitionHandle, byte[]> expectedPayloadHashes = options.VerifyPayloadHash
                ? LpVerifier.ComputeExpectedHashesBeforeWrite(
                    plan,
                    devices,
                    session.WriteLimits.DataBufferSize,
                    cancellationToken)
                : [];

            Dictionary<LpPartitionHandle, IncrementalHash> imageHashers =
                options.VerifyPayloadHash
                    ? plan.DataWrites
                        .Where(write => write.Kind == LpDataWriteKind.Image)
                        .Select(write => write.Partition)
                        .Distinct()
                        .ToDictionary(
                            handle => handle,
                            _ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    : [];
            long writtenBytes = 0;
            HashSet<uint> writtenDevices = [];
            try
            {
                writtenBytes = await WriteDataAsync(
                        plan,
                        devices,
                        imageHashers,
                        writtenDevices,
                        session.WriteLimits.DataBufferSize,
                        () =>
                        {
                            dataAccepted = true;
                            durability = CombineDurability(
                                durability,
                                DurabilityLevel.WriteAccepted);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach ((LpPartitionHandle handle, IncrementalHash hasher) in imageHashers)
                {
                    expectedPayloadHashes[handle] = hasher.GetHashAndReset();
                }
            }
            catch (OperationCanceledException exception)
            {
                if (!dataAccepted)
                {
                    throw;
                }
                throw Failure(
                    LpCommitErrorCode.CancelledAfterDataWrite,
                    phase,
                    metadataChanged,
                    plan.PublicPlan.DataRollbackState,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            catch (LpCommitException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.DataWriteFailed,
                    phase,
                    metadataChanged,
                    dataAccepted ? plan.PublicPlan.DataRollbackState : DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            finally
            {
                foreach (IncrementalHash hasher in imageHashers.Values)
                {
                    hasher.Dispose();
                }
            }
            phase = LpCommitPhase.DataWritten;

            try
            {
                DurabilityLevel flushedDataDurability = DurabilityLevel.None;
                foreach (uint index in writtenDevices.Order())
                {
                    devices[index].Flush();
                    flushedDataDurability = CombineDurability(
                        flushedDataDurability,
                        GetFlushDurability(devices[index]));
                }
                if (writtenDevices.Count != 0)
                {
                    durability = flushedDataDurability;
                }
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.DataFlushFailed,
                    phase,
                    metadataChanged,
                    dataAccepted ? plan.PublicPlan.DataRollbackState : DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            phase = LpCommitPhase.DataFlushed;
            LpLog.DataPhaseCompleted(logger, writtenBytes);

            try
            {
                unchanged = IsSnapshotUnchanged(metadataDevice, session, cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                if (!dataAccepted)
                {
                    throw;
                }
                throw Failure(
                    LpCommitErrorCode.CancelledAfterDataWrite,
                    phase,
                    metadataChanged,
                    plan.PublicPlan.DataRollbackState,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.MetadataReadFailed,
                    phase,
                    metadataChanged,
                    dataAccepted ? plan.PublicPlan.DataRollbackState : DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            if (!unchanged)
            {
                throw Failure(
                    dataAccepted
                        ? LpCommitErrorCode.SourceChangedAfterDataWrite
                        : LpCommitErrorCode.SourceChangedBeforeWrite,
                    phase,
                    metadataChanged,
                    dataAccepted ? plan.PublicPlan.DataRollbackState : DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability);
            }
            phase = LpCommitPhase.SourceRevalidated;

            if (cancellationToken.IsCancellationRequested)
            {
                if (!dataAccepted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw Failure(
                    LpCommitErrorCode.CancelledAfterDataWrite,
                    phase,
                    metadataChanged,
                    plan.PublicPlan.DataRollbackState,
                    primaryState,
                    backupState,
                    durability,
                    new OperationCanceledException(cancellationToken));
            }

            LpCopySnapshot primaryCopy = session.Snapshot.Slots[session.SlotNumber][0];
            LpCopySnapshot backupCopy = session.Snapshot.Slots[session.SlotNumber][1];
            LpCopySnapshot selected = session.Snapshot.BaselineKind == LpMetadataCopyKind.Primary
                ? primaryCopy
                : backupCopy;
            LpCopySnapshot other = selected.Kind == LpMetadataCopyKind.Primary
                ? backupCopy
                : primaryCopy;
            bool repairOldCopy = !other.IsValid ||
                !CryptographicOperations.FixedTimeEquals(selected.Digest, other.Digest);
            if (repairOldCopy)
            {
                DurabilityLevel durabilityBeforeRepair = durability;
                try
                {
                    metadataChanged = true;
                    metadataDevice.WriteAt(
                        LpMetadataSet.GetMetadataOffset(
                            session.Geometry,
                            session.SlotNumber,
                            other.Kind),
                        plan.BaselineMetadata);
                    durability = CombineDurability(
                        durabilityBeforeRepair,
                        DurabilityLevel.WriteAccepted);
                    if (other.Kind == LpMetadataCopyKind.Primary)
                    {
                        primaryState = MetadataCopyState.Repaired;
                    }
                    else
                    {
                        backupState = MetadataCopyState.Repaired;
                    }
                    metadataDevice.Flush();
                    durability = CombineDurability(
                        durabilityBeforeRepair,
                        GetFlushDurability(metadataDevice));
                    phase = LpCommitPhase.OldMetadataRepaired;
                    LpLog.OldCopyRepaired(logger, other.Kind);
                }
                catch (Exception exception)
                {
                    throw Failure(
                        LpCommitErrorCode.OldMetadataRepairFailed,
                        phase,
                        metadataChanged,
                        dataAccepted ? plan.PublicPlan.DataRollbackState : DataRollbackState.NotRequired,
                        primaryState,
                        backupState,
                        durability,
                        exception);
                }
            }

            long primaryOffset = LpMetadataSet.GetMetadataOffset(
                session.Geometry,
                session.SlotNumber,
                LpMetadataCopyKind.Primary);
            DurabilityLevel durabilityBeforePrimary = durability;
            try
            {
                metadataChanged = true;
                metadataDevice.WriteAt(primaryOffset, plan.NewMetadata);
                durability = CombineDurability(
                    durabilityBeforePrimary,
                    DurabilityLevel.WriteAccepted);
                primaryState = MetadataCopyState.NewWritten;
                phase = LpCommitPhase.PrimaryMetadataWritten;
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.PrimaryMetadataWriteFailed,
                    phase,
                    metadataChanged,
                    dataAccepted ? plan.PublicPlan.DataRollbackState : DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            try
            {
                metadataDevice.Flush();
                durability = CombineDurability(
                    durabilityBeforePrimary,
                    GetFlushDurability(metadataDevice));
                primaryState = MetadataCopyState.NewDurable;
                phase = LpCommitPhase.PrimaryMetadataFlushed;
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.PrimaryMetadataFlushFailed,
                    phase,
                    metadataChanged,
                    dataAccepted ? plan.PublicPlan.DataRollbackState : DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }

            long backupOffset = LpMetadataSet.GetMetadataOffset(
                session.Geometry,
                session.SlotNumber,
                LpMetadataCopyKind.Backup);
            DurabilityLevel durabilityBeforeBackup = durability;
            try
            {
                metadataDevice.WriteAt(backupOffset, plan.NewMetadata);
                durability = CombineDurability(
                    durabilityBeforeBackup,
                    DurabilityLevel.WriteAccepted);
                backupState = MetadataCopyState.NewWritten;
                phase = LpCommitPhase.BackupMetadataWritten;
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.BackupMetadataWriteFailed,
                    phase,
                    metadataChanged,
                    dataAccepted ? plan.PublicPlan.DataRollbackState : DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            try
            {
                metadataDevice.Flush();
                durability = CombineDurability(
                    durabilityBeforeBackup,
                    GetFlushDurability(metadataDevice));
                backupState = MetadataCopyState.NewDurable;
                phase = LpCommitPhase.BackupMetadataFlushed;
            }
            catch (Exception exception)
            {
                throw Failure(
                    LpCommitErrorCode.BackupMetadataFlushFailed,
                    phase,
                    metadataChanged,
                    dataAccepted ? plan.PublicPlan.DataRollbackState : DataRollbackState.NotRequired,
                    primaryState,
                    backupState,
                    durability,
                    exception);
            }
            LpLog.MetadataCommitted(logger, session.SlotNumber);

            VerificationStatus verificationStatus = VerificationStatus.NotRequested;
            LpCommitIssue? verificationIssue = null;
            if (options.VerifyAfterCommit)
            {
                try
                {
                    LpVerifier.VerifyMetadata(
                        metadataDevice,
                        session,
                        plan,
                        cancellationToken);
                    phase = LpCommitPhase.MetadataVerified;
                    if (options.VerifyPayloadHash)
                    {
                        LpVerifier.VerifyPayloadHashes(
                            plan,
                            devices,
                            expectedPayloadHashes,
                            session.WriteLimits.DataBufferSize,
                            cancellationToken);
                        phase = LpCommitPhase.PayloadVerified;
                    }
                    verificationStatus = VerificationStatus.Succeeded;
                }
                catch (OperationCanceledException exception)
                {
                    verificationStatus = VerificationStatus.Cancelled;
                    verificationIssue = Issue(
                        LpCommitErrorCode.VerificationCancelled,
                        phase,
                        exception);
                }
                catch (Exception exception)
                {
                    verificationStatus = VerificationStatus.Failed;
                    verificationIssue = Issue(
                        options.VerifyPayloadHash && phase == LpCommitPhase.MetadataVerified
                            ? LpCommitErrorCode.PayloadVerificationFailed
                            : LpCommitErrorCode.MetadataVerificationFailed,
                        phase,
                        exception);
                }
            }
            if (verificationStatus is VerificationStatus.Failed or VerificationStatus.Cancelled)
            {
                LpLog.VerificationIncomplete(logger, verificationStatus);
            }

            CleanupStatus cleanupStatus = CleanupStatus.NotRequested;
            LpCommitIssue? cleanupIssue = null;
            if (options.WipeFreedExtents)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WipeFreedExtents(
                        plan.FreedRanges,
                        devices,
                        session.WriteLimits.DataBufferSize,
                        cancellationToken);
                    cleanupStatus = CleanupStatus.Succeeded;
                    phase = LpCommitPhase.CleanupCompleted;
                }
                catch (OperationCanceledException exception)
                {
                    cleanupStatus = CleanupStatus.Cancelled;
                    cleanupIssue = Issue(
                        LpCommitErrorCode.CleanupCancelled,
                        phase,
                        exception);
                }
                catch (Exception exception)
                {
                    cleanupStatus = CleanupStatus.Failed;
                    cleanupIssue = Issue(
                        LpCommitErrorCode.CleanupFailed,
                        phase,
                        exception);
                }
            }
            if (cleanupStatus is CleanupStatus.Failed or CleanupStatus.Cancelled)
            {
                LpLog.CleanupIncomplete(logger, cleanupStatus);
            }

            return new LpCommitResult(
                CommitStatus.Succeeded,
                verificationStatus,
                cleanupStatus,
                phase,
                primaryState,
                backupState,
                durability,
                verificationIssue,
                cleanupIssue);
        }
        finally
        {
            var disposedLeases = new HashSet<IWritableBlockDeviceLease>(
                ReferenceEqualityComparer.Instance);
            for (int index = leases.Count - 1; index >= 0; index--)
            {
                if (!disposedLeases.Add(leases[index].Lease))
                {
                    continue;
                }
                try
                {
                    leases[index].Lease.Dispose();
                }
                catch (Exception exception)
                {
                    LpLog.LeaseDisposeFailed(logger, leases[index].Index, exception);
                }
            }
        }
    }

    private static async ValueTask<long> WriteDataAsync(
        LpPlanState plan,
        IReadOnlyDictionary<uint, IWritableBlockDevice> devices,
        IReadOnlyDictionary<LpPartitionHandle, IncrementalHash> imageHashers,
        HashSet<uint> writtenDevices,
        int bufferSize,
        Action writeAccepted,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var streams = new Dictionary<LpPartitionImageSource, Stream>(
            ReferenceEqualityComparer.Instance);
        var positions = new Dictionary<LpPartitionImageSource, long>(
            ReferenceEqualityComparer.Instance);
        long total = 0;
        try
        {
            foreach (LpDataWrite write in plan.DataWrites)
            {
                long completed = 0;
                while (completed < write.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int partLength = (int)Math.Min(buffer.Length, write.Length - completed);
                    Memory<byte> part = buffer.AsMemory(0, partLength);
                    if (write.Kind == LpDataWriteKind.Zero)
                    {
                        part.Span.Clear();
                    }
                    else
                    {
                        LpPartitionImageSource image = write.Image!;
                        if (!streams.TryGetValue(image, out Stream? stream))
                        {
                            stream = image.OpenExpandedStream();
                            streams.Add(image, stream);
                            positions.Add(image, 0);
                        }
                        long requestedOffset = checked(write.SourceOffset + completed);
                        long current = positions[image];
                        if (current != requestedOffset)
                        {
                            if (!stream.CanSeek)
                            {
                                throw new IOException(Resources.ImageSourceConsumed);
                            }
                            stream.Position = requestedOffset;
                            current = requestedOffset;
                        }
                        int readTotal = 0;
                        while (readTotal < partLength)
                        {
                            int read = await stream
                                .ReadAsync(part[readTotal..], cancellationToken)
                                .ConfigureAwait(false);
                            if ((uint)read > (uint)(partLength - readTotal))
                            {
                                throw new IOException(
                                    Resources.InvalidStreamReadResult);
                            }
                            if (read == 0)
                            {
                                throw new EndOfStreamException(
                                    Resources.ImageSourceConsumed);
                            }
                            readTotal += read;
                        }
                        positions[image] = checked(current + partLength);
                    }

                    if (imageHashers.TryGetValue(write.Partition, out IncrementalHash? hasher))
                    {
                        hasher.AppendData(part.Span);
                    }
                    devices[write.TargetSource].WriteAt(
                        checked(write.TargetOffset + completed),
                        part.Span);
                    writeAccepted();
                    writtenDevices.Add(write.TargetSource);
                    completed += partLength;
                    total = checked(total + partLength);
                }
            }
            return total;
        }
        finally
        {
            foreach (Stream stream in streams.Values)
            {
                stream.Dispose();
            }
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void WipeFreedExtents(
        ReadOnlySpan<LpSectorRange> ranges,
        IReadOnlyDictionary<uint, IWritableBlockDevice> devices,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        buffer.AsSpan(0, bufferSize).Clear();
        var written = new HashSet<uint>();
        try
        {
            foreach (LpSectorRange range in ranges)
            {
                long offset = checked((long)range.StartSector * LpFormat.SectorSize);
                long length = checked((long)range.SectorCount * LpFormat.SectorSize);
                long completed = 0;
                while (completed < length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int partLength = (int)Math.Min(bufferSize, length - completed);
                    devices[range.TargetSource].WriteAt(
                        checked(offset + completed),
                        buffer.AsSpan(0, partLength));
                    written.Add(range.TargetSource);
                    completed += partLength;
                }
            }
            foreach (uint index in written.Order())
            {
                devices[index].Flush();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static SortedSet<uint> GetRequiredDeviceIndices(
        LpPlanState plan,
        LpCommitOptions options)
    {
        var result = new SortedSet<uint> { 0 };
        foreach (LpExtent extent in plan.FinalModel.Extents)
        {
            if (extent.TargetType == LpExtentTargetType.Linear)
            {
                result.Add(extent.TargetSource);
            }
        }
        if (options.WipeFreedExtents)
        {
            foreach (LpSectorRange range in plan.FreedRanges)
            {
                result.Add(range.TargetSource);
            }
        }
        return result;
    }

    private static bool IsSnapshotUnchanged(
        IReadableBlockDevice source,
        LpEditSession session,
        CancellationToken cancellationToken)
    {
        if (source.Id != session.Snapshot.MetadataDevice.Id ||
            source.Length != session.Snapshot.MetadataDevice.Length ||
            source.LogicalBlockSize != session.Snapshot.MetadataDevice.LogicalBlockSize)
        {
            return false;
        }
        byte[] primaryGeometry = LpSnapshotReader.HashRange(
            source,
            LpFormat.ReservedBytes,
            LpFormat.GeometryBlockSize,
            cancellationToken);
        byte[] backupGeometry = LpSnapshotReader.HashRange(
            source,
            LpFormat.ReservedBytes + LpFormat.GeometryBlockSize,
            LpFormat.GeometryBlockSize,
            cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                primaryGeometry,
                session.Snapshot.PrimaryGeometryDigest) ||
            !CryptographicOperations.FixedTimeEquals(
                backupGeometry,
                session.Snapshot.BackupGeometryDigest))
        {
            return false;
        }
        foreach (LpCopySnapshot[] slot in session.Snapshot.Slots)
        {
            foreach (LpCopySnapshot copy in slot)
            {
                byte[] digest = LpSnapshotReader.HashMetadataCopy(
                    source,
                    session.Geometry,
                    copy.SlotNumber,
                    copy.Kind,
                    cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(digest, copy.Digest))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static LpPartitionImageSource[] GetDistinctImages(
        IReadOnlyList<LpDataWrite> writes)
    {
        var distinct = new HashSet<LpPartitionImageSource>(
            ReferenceEqualityComparer.Instance);
        foreach (LpDataWrite write in writes)
        {
            if (write.Image is LpPartitionImageSource image)
            {
                distinct.Add(image);
            }
        }

        return distinct.ToArray();
    }

    private static DurabilityLevel GetFlushDurability(IWritableBlockDevice device) =>
        device is IBlockDeviceFlushDurability durability
            ? durability.FlushDurability switch
            {
                BlockDeviceFlushDurability.WriteAccepted => DurabilityLevel.WriteAccepted,
                BlockDeviceFlushDurability.ProtocolAcknowledged =>
                    DurabilityLevel.ProtocolAcknowledged,
                BlockDeviceFlushDurability.FlushToDisk => DurabilityLevel.FlushToDisk,
                _ => DurabilityLevel.WriteAccepted
            }
            : DurabilityLevel.WriteAccepted;

    private static DurabilityLevel CombineDurability(
        DurabilityLevel left,
        DurabilityLevel right)
    {
        if (left == DurabilityLevel.None)
        {
            return right;
        }
        if (right == DurabilityLevel.None)
        {
            return left;
        }
        return (DurabilityLevel)Math.Min((int)left, (int)right);
    }

    private static LpCommitIssue Issue(
        LpCommitErrorCode code,
        LpCommitPhase phase,
        Exception exception) =>
        new(
            code,
            $"Lp.Commit.{code}",
            diagnosticContext: new LpDiagnosticContext(phase: phase),
            innerException: exception);

    private static LpCommitException Failure(
        LpCommitErrorCode code,
        LpCommitPhase phase,
        bool metadataChanged,
        DataRollbackState rollbackState,
        MetadataCopyState primaryState,
        MetadataCopyState backupState,
        DurabilityLevel durability,
        Exception? exception = null) =>
        new(new LpCommitFailure(
            code,
            phase,
            metadataChanged,
            rollbackState,
            primaryState,
            backupState,
            durability,
            $"Lp.Commit.{code}",
            diagnosticContext: new LpDiagnosticContext(phase: phase),
            innerException: exception));
}
