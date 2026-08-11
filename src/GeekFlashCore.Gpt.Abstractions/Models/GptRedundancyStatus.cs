namespace GeekFlashCore.Gpt.Abstractions;

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
