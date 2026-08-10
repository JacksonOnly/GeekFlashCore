namespace GeekFlashCore.Gpt.Abstractions;

public enum GptHeaderCopy
{
    None,
    Primary,
    Backup
}

public sealed record GptCopyStatus(
    bool Present,
    bool HeaderCrcValid,
    bool? PartitionEntryArrayValid,
    string? Error = null)
{
    public bool IsUsable =>
        Present && HeaderCrcValid && PartitionEntryArrayValid is not false && Error is null;
}

public sealed record GptRedundancyStatus(
    GptCopyStatus Primary,
    GptCopyStatus Backup,
    GptHeaderCopy ActiveCopy,
    bool? HeadersConsistent,
    bool? PartitionEntryArraysConsistent)
{
    public bool HasUsableCopy => Primary.IsUsable || Backup.IsUsable;

    public bool? CopiesConsistent =>
        HeadersConsistent is false || PartitionEntryArraysConsistent is false
            ? false
            : HeadersConsistent is true && PartitionEntryArraysConsistent is true
                ? true
                : null;
}
