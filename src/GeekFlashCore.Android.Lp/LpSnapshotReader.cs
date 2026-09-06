using System.Buffers;
using System.Security.Cryptography;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal static class LpSnapshotReader
{
    private const uint KnownHeaderFlags = 0x00000003;
    private const int HashBufferSize = 64 * 1024;

    internal static LpSessionSnapshot Capture(
        LpMetadataSet set,
        int targetSlot,
        IReadOnlyDictionary<uint, LpDeviceIdentity>? resolvedDevices = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        if ((uint)targetSlot >= (uint)set.Slots.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSlot));
        }

        if (set.PrimaryGeometry.Geometry is LpGeometry primaryGeometry &&
            set.BackupGeometry.Geometry is LpGeometry backupGeometry &&
            primaryGeometry != backupGeometry)
        {
            throw new InvalidDataException(Resources.GeometryCopiesDiffer);
        }

        byte[] primaryGeometryDigest = HashRange(
            set.Source,
            LpFormat.ReservedBytes,
            LpFormat.GeometryBlockSize,
            cancellationToken);
        byte[] backupGeometryDigest = HashRange(
            set.Source,
            LpFormat.ReservedBytes + LpFormat.GeometryBlockSize,
            LpFormat.GeometryBlockSize,
            cancellationToken);

        var slots = new LpCopySnapshot[set.Slots.Count][];
        LpBlockDevice[]? canonicalBlockDevices = null;
        for (int slot = 0; slot < slots.Length; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            slots[slot] =
            [
                CaptureCopy(set, slot, LpMetadataCopyKind.Primary, cancellationToken),
                CaptureCopy(set, slot, LpMetadataCopyKind.Backup, cancellationToken)
            ];

            foreach (LpCopySnapshot copy in slots[slot])
            {
                if (!copy.IsValid)
                {
                    continue;
                }

                LpTableModel model = copy.Model!;
                if ((model.Header.Flags & ~KnownHeaderFlags) != 0)
                {
                    throw new NotSupportedException(
                        Resources.FormatUnsupportedHeaderFlags(
                            model.Header.Flags.ToString("X8", CultureInfo.InvariantCulture)));
                }

                ValidateRewritableNames(model);
                if (canonicalBlockDevices is null)
                {
                    canonicalBlockDevices = model.BlockDevices;
                }
                else if (!canonicalBlockDevices.AsSpan().SequenceEqual(model.BlockDevices))
                {
                    throw new InvalidDataException(Resources.BlockDeviceTablesDiffer);
                }
            }
        }

        LpCopySnapshot? baselineCopy = slots[targetSlot][0].IsValid
            ? slots[targetSlot][0]
            : slots[targetSlot][1].IsValid
                ? slots[targetSlot][1]
                : null;
        if (baselineCopy is null)
        {
            throw new InvalidDataException(Resources.TargetSlotUnavailable);
        }

        var metadataIdentity = new LpDeviceIdentity(
            set.Source.Id,
            set.Source.Length,
            set.Source.LogicalBlockSize);
        var identities = resolvedDevices is null
            ? new Dictionary<uint, LpDeviceIdentity> { [0] = metadataIdentity }
            : new Dictionary<uint, LpDeviceIdentity>(resolvedDevices);
        identities[0] = metadataIdentity;

        return new LpSessionSnapshot(
            set.Geometry,
            primaryGeometryDigest,
            backupGeometryDigest,
            slots,
            baselineCopy.Model!,
            baselineCopy.Kind,
            metadataIdentity,
            identities);
    }

    internal static byte[] HashMetadataCopy(
        IReadableBlockDevice source,
        LpGeometry geometry,
        int slot,
        LpMetadataCopyKind kind,
        CancellationToken cancellationToken = default) =>
        HashRange(
            source,
            LpMetadataSet.GetMetadataOffset(geometry, slot, kind),
            checked((int)geometry.MetadataMaxSize),
            cancellationToken);

    internal static byte[] HashRange(
        IReadableBlockDevice source,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > source.Length - length)
        {
            throw new EndOfStreamException(Resources.UnexpectedEndOfStream);
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(HashBufferSize, Math.Max(1, length)));
        try
        {
            long current = offset;
            int remaining = length;
            while (remaining != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int partLength = Math.Min(remaining, buffer.Length);
                BlockDeviceIO.ReadExactlyAt(source, current, buffer.AsSpan(0, partLength));
                hash.AppendData(buffer, 0, partLength);
                current = checked(current + partLength);
                remaining -= partLength;
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static LpCopySnapshot CaptureCopy(
        LpMetadataSet set,
        int slot,
        LpMetadataCopyKind kind,
        CancellationToken cancellationToken)
    {
        LpMetadataCopySummary summary = kind == LpMetadataCopyKind.Primary
            ? set.Slots[slot].Primary
            : set.Slots[slot].Backup;
        byte[] digest = HashMetadataCopy(set.Source, set.Geometry, slot, kind, cancellationToken);
        if (summary.Status != LpCopyValidationStatus.Valid)
        {
            return new LpCopySnapshot(slot, kind, summary.Status, digest, null);
        }

        using LpMetadataDocument document = set.OpenCopy(slot, kind);
        var model = new LpTableModel(
            document.Header,
            document.Partitions.ToArray(),
            document.Extents.ToArray(),
            document.Groups.ToArray(),
            document.BlockDevices.ToArray());
        return new LpCopySnapshot(slot, kind, summary.Status, digest, model);
    }

    private static void ValidateRewritableNames(LpTableModel model)
    {
        foreach (LpPartition partition in model.Partitions)
        {
            ValidateName(partition.RawName);
        }

        foreach (LpPartitionGroup group in model.Groups)
        {
            ValidateName(group.RawName);
        }

        foreach (LpBlockDevice device in model.BlockDevices)
        {
            ValidateName(device.RawPartitionName);
        }
    }

    private static void ValidateName(string name)
    {
        if (!LpNameValidator.IsValid(name))
        {
            throw new InvalidDataException(Resources.FormatInvalidSourceNameEncoding(name));
        }
    }
}
