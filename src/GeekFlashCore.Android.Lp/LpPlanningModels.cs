using GeekFlashCore.Android.Lp.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal readonly record struct LpSectorRange(
    uint TargetSource,
    ulong StartSector,
    ulong SectorCount)
{
    internal ulong EndSector => checked(StartSector + SectorCount);
}

internal enum LpDataWriteKind
{
    Image,
    Zero
}

internal sealed record LpDataWrite(
    LpPartitionHandle Partition,
    string PartitionName,
    LpDataWriteKind Kind,
    LpPartitionImageSource? Image,
    long SourceOffset,
    uint TargetSource,
    long TargetOffset,
    long Length);

internal sealed record LpFinalModel(
    LpMetadataHeader SourceHeader,
    LpPartition[] Partitions,
    LpExtent[] Extents,
    LpPartitionGroup[] Groups,
    LpBlockDevice[] BlockDevices);

internal sealed class LpPlanState
{
    internal required LpCommitPlan PublicPlan { get; init; }
    internal required LpFinalModel FinalModel { get; init; }
    internal required byte[] NewMetadata { get; init; }
    internal required IReadOnlyDictionary<LpPartitionHandle, int> PartitionIndices { get; init; }
    internal required byte[] BaselineMetadata { get; init; }
    internal required LpDataWrite[] DataWrites { get; init; }
    internal required LpSectorRange[] FreedRanges { get; init; }
    internal required LpPartitionHandle[] ChangedPartitions { get; init; }
}
