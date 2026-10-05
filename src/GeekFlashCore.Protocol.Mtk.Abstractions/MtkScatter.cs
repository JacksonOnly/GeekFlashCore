namespace GeekFlashCore.Protocol.Mtk.Abstractions;

public enum MtkScatterOperation { Bootloaders,Invisible,Update,Protected,BinRegion,Reserved,Logic,NeedResize,RebaseResize }
/// <summary>Validated manifest entry. Region zero explicitly mirrors BOOT1/BOOT2 or LU0/LU1; no default region.</summary>
public sealed record MtkScatterPartition(string Name,string? FileName,bool Download,MtkStorageKind Storage,
    uint RegionId,ulong Offset,ulong Length,MtkScatterOperation Operation,uint? Host=null);
/// <summary>Immutable validated scatter manifest; filenames are bundle-relative.</summary>
public sealed record MtkScatterManifest(IReadOnlyList<MtkScatterPartition> Partitions);
/// <summary>Resolved real storage range, after reserved-tail and NEEDRESIZE handling.</summary>
public sealed record MtkScatterPlannedPartition(string Name,string? FileName,bool Download,MtkFlashRange Range,MtkScatterOperation Operation);
/// <summary>Generation-bound reviewable plan. Applying never reads arbitrary filenames requested by the device.</summary>
public sealed record MtkScatterPlan(long Generation,IReadOnlyList<MtkScatterPlannedPartition> Partitions);
/// <summary>Optional synchronous partition snapshot inside the existing DA gate.</summary>
public interface IMtkDaPartitionChannel
{
    IReadOnlyList<MtkPartitionRange> GetPartitionRanges();
    /// <summary>Native bootloader/header-aware write within the same gate. Stream is borrowed and already preflighted.</summary>
    void WriteNamedPartition(string name,Stream source,long length)=>throw new MtkCapabilityException("native partition channel");
}
public sealed record MtkPartitionRange(string Name,MtkFlashRange Range);
/// <summary>Durable backup storage. OpenRead must return the exact previously persisted range.</summary>
public interface IMtkScatterBackupStore
{
    Stream Create(string name);
    GeekFlashCore.Protocol.Abstractions.IDataSource OpenRead(string name);
}
public sealed class MtkScatterWriteException(Exception inner) : GeekFlashCore.Protocol.Abstractions.ProtocolException(Localization.Strings.ScatterWriteUnknown,inner)
{
    public bool MayHaveWritten=>true;
}
