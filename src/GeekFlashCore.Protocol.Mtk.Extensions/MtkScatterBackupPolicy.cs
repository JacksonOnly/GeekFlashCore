namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Host backup and migration scope for a Scatter operation.</summary>
public enum MtkScatterBackupPolicy
{
    /// <summary>Backs up overwritten ranges and preserves protected partitions during GPT rebuild.</summary>
    AllOverwriteRanges,
    /// <summary>Backs up only the current GPT when changing it; partition contents are not backed up or migrated.</summary>
    PartitionTableOnly
}
