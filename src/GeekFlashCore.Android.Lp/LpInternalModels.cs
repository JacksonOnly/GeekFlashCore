using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal sealed record LpTableModel(
    LpMetadataHeader Header,
    LpPartition[] Partitions,
    LpExtent[] Extents,
    LpPartitionGroup[] Groups,
    LpBlockDevice[] BlockDevices);

internal sealed record LpCopySnapshot(
    int SlotNumber,
    LpMetadataCopyKind Kind,
    LpCopyValidationStatus Status,
    byte[] Digest,
    LpTableModel? Model)
{
    internal bool IsValid => Status == LpCopyValidationStatus.Valid && Model is not null;
}

internal readonly record struct LpDeviceIdentity(
    BlockDeviceId Id,
    long Length,
    int LogicalBlockSize);

internal sealed class LpSessionSnapshot
{
    internal LpSessionSnapshot(
        LpGeometry geometry,
        byte[] primaryGeometryDigest,
        byte[] backupGeometryDigest,
        LpCopySnapshot[][] slots,
        LpTableModel baseline,
        LpMetadataCopyKind baselineKind,
        LpDeviceIdentity metadataDevice,
        IReadOnlyDictionary<uint, LpDeviceIdentity> resolvedDevices)
    {
        Geometry = geometry;
        PrimaryGeometryDigest = primaryGeometryDigest;
        BackupGeometryDigest = backupGeometryDigest;
        Slots = slots;
        Baseline = baseline;
        BaselineKind = baselineKind;
        MetadataDevice = metadataDevice;
        ResolvedDevices = resolvedDevices;
    }

    internal LpGeometry Geometry { get; }
    internal byte[] PrimaryGeometryDigest { get; }
    internal byte[] BackupGeometryDigest { get; }
    internal LpCopySnapshot[][] Slots { get; }
    internal LpTableModel Baseline { get; }
    internal LpMetadataCopyKind BaselineKind { get; }
    internal LpDeviceIdentity MetadataDevice { get; }
    internal IReadOnlyDictionary<uint, LpDeviceIdentity> ResolvedDevices { get; }
}

internal sealed record LpPartitionState(
    LpPartitionHandle Handle,
    string RawName,
    LpPartitionAttributes Attributes,
    string GroupRawName,
    long RequestedSize,
    LpExtent[] OriginalExtents,
    LpPartitionImageSource? ReplacementImage,
    bool IsNew);

internal sealed record LpGroupState(
    LpGroupHandle Handle,
    string RawName,
    LpGroupFlags Flags,
    ulong MaximumSize,
    bool IsDefault,
    bool IsNew);

internal sealed record LpDraftSnapshot(
    LpPartitionState[] Partitions,
    LpGroupState[] Groups,
    LpPartitionState[] OriginalPartitions);
