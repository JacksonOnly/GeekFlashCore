using System.Buffers;
using System.Security.Cryptography;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal static class LpVerifier
{
    internal static void VerifyMetadata(
        IReadableBlockDevice metadataDevice,
        LpEditSession session,
        LpPlanState plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (metadataDevice.Id != session.Snapshot.MetadataDevice.Id ||
            metadataDevice.Length != session.Snapshot.MetadataDevice.Length ||
            metadataDevice.LogicalBlockSize != session.Snapshot.MetadataDevice.LogicalBlockSize)
        {
            throw new InvalidDataException(Resources.DeviceIdentityMismatch);
        }

        byte[] primaryGeometry = LpSnapshotReader.HashRange(
            metadataDevice,
            LpFormat.ReservedBytes,
            LpFormat.GeometryBlockSize,
            cancellationToken);
        byte[] backupGeometry = LpSnapshotReader.HashRange(
            metadataDevice,
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
            throw new InvalidDataException(Resources.MetadataVerificationFailed);
        }

        byte[] expectedMetadataDigest = SHA256.HashData(plan.NewMetadata);
        byte[] primaryDigest = LpSnapshotReader.HashMetadataCopy(
            metadataDevice,
            session.Geometry,
            session.SlotNumber,
            LpMetadataCopyKind.Primary,
            cancellationToken);
        byte[] backupDigest = LpSnapshotReader.HashMetadataCopy(
            metadataDevice,
            session.Geometry,
            session.SlotNumber,
            LpMetadataCopyKind.Backup,
            cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(expectedMetadataDigest, primaryDigest) ||
            !CryptographicOperations.FixedTimeEquals(expectedMetadataDigest, backupDigest))
        {
            throw new InvalidDataException(Resources.MetadataVerificationFailed);
        }

        foreach (LpCopySnapshot[] slot in session.Snapshot.Slots)
        {
            foreach (LpCopySnapshot copy in slot)
            {
                if (copy.SlotNumber == session.SlotNumber)
                {
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                byte[] current = LpSnapshotReader.HashMetadataCopy(
                    metadataDevice,
                    session.Geometry,
                    copy.SlotNumber,
                    copy.Kind,
                    cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(current, copy.Digest))
                {
                    throw new InvalidDataException(Resources.MetadataVerificationFailed);
                }
            }
        }

        using LpMetadataSet set = LpMetadataSet.Open(
            metadataDevice,
            DeviceOwnership.Borrow,
            session.ReadLimits);
        LpMetadataSlotSummary target = set.Slots[session.SlotNumber];
        if (target.Primary.Status != LpCopyValidationStatus.Valid ||
            target.Backup.Status != LpCopyValidationStatus.Valid)
        {
            throw new InvalidDataException(Resources.MetadataVerificationFailed);
        }

        using LpMetadataDocument document = set.OpenCopy(
            session.SlotNumber,
            LpMetadataCopyKind.Primary);
        LpFinalModel expected = plan.FinalModel;
        if (document.Header.MajorVersion != expected.SourceHeader.MajorVersion ||
            document.Header.MinorVersion != expected.SourceHeader.MinorVersion ||
            document.Header.Flags != expected.SourceHeader.Flags ||
            !document.Partitions.Span.SequenceEqual(expected.Partitions) ||
            !document.Extents.Span.SequenceEqual(expected.Extents) ||
            !document.Groups.Span.SequenceEqual(expected.Groups) ||
            !document.BlockDevices.Span.SequenceEqual(expected.BlockDevices))
        {
            throw new InvalidDataException(Resources.MetadataVerificationFailed);
        }
    }

    internal static Dictionary<LpPartitionHandle, byte[]> ComputeExpectedHashesBeforeWrite(
        LpPlanState plan,
        IReadOnlyDictionary<uint, IWritableBlockDevice> devices,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<LpPartitionHandle, byte[]>();
        HashSet<LpPartitionHandle> imagePartitions = plan.DataWrites
            .Where(write => write.Kind == LpDataWriteKind.Image)
            .Select(write => write.Partition)
            .ToHashSet();
        foreach (LpPartitionHandle handle in plan.ChangedPartitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (imagePartitions.Contains(handle))
            {
                continue;
            }
            int partitionIndex = plan.PartitionIndices[handle];
            result[handle] = HashPartition(
                plan,
                partitionIndex,
                devices,
                plan.DataWrites.Where(write =>
                    write.Partition == handle && write.Kind == LpDataWriteKind.Zero).ToArray(),
                bufferSize,
                cancellationToken);
        }
        return result;
    }

    internal static void VerifyPayloadHashes(
        LpPlanState plan,
        IReadOnlyDictionary<uint, IWritableBlockDevice> devices,
        IReadOnlyDictionary<LpPartitionHandle, byte[]> expectedHashes,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        foreach ((LpPartitionHandle handle, byte[] expected) in expectedHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int partitionIndex = plan.PartitionIndices[handle];
            byte[] actual = HashPartition(
                plan,
                partitionIndex,
                devices,
                zeroOverrides: [],
                bufferSize,
                cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException(Resources.PayloadVerificationFailed);
            }
        }
    }

    private static byte[] HashPartition(
        LpPlanState plan,
        int partitionIndex,
        IReadOnlyDictionary<uint, IWritableBlockDevice> devices,
        LpDataWrite[] zeroOverrides,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        LpPartition partition = plan.FinalModel.Partitions[partitionIndex];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            for (uint relative = 0; relative < partition.ExtentCount; relative++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LpExtent extent = plan.FinalModel.Extents[
                    checked((int)(partition.FirstExtentIndex + relative))];
                long length = checked((long)extent.SectorCount * LpFormat.SectorSize);
                long completed = 0;
                while (completed < length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int partLength = (int)Math.Min(buffer.Length, length - completed);
                    Span<byte> part = buffer.AsSpan(0, partLength);
                    if (extent.TargetType == LpExtentTargetType.Zero)
                    {
                        part.Clear();
                    }
                    else
                    {
                        long physicalOffset = checked(
                            (long)extent.TargetData * LpFormat.SectorSize + completed);
                        BlockDeviceIO.ReadExactlyAt(
                            devices[extent.TargetSource],
                            physicalOffset,
                            part);
                        ApplyZeroOverrides(
                            part,
                            extent.TargetSource,
                            physicalOffset,
                            zeroOverrides);
                    }
                    hash.AppendData(part);
                    completed += partLength;
                }
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ApplyZeroOverrides(
        Span<byte> destination,
        uint targetSource,
        long physicalOffset,
        ReadOnlySpan<LpDataWrite> overrides)
    {
        long end = checked(physicalOffset + destination.Length);
        foreach (LpDataWrite write in overrides)
        {
            if (write.TargetSource != targetSource)
            {
                continue;
            }
            long writeEnd = checked(write.TargetOffset + write.Length);
            long start = Math.Max(physicalOffset, write.TargetOffset);
            long overlapEnd = Math.Min(end, writeEnd);
            if (start < overlapEnd)
            {
                destination.Slice(
                    checked((int)(start - physicalOffset)),
                    checked((int)(overlapEnd - start))).Clear();
            }
        }
    }
}
