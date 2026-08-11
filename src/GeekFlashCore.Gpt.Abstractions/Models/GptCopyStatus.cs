namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptCopyStatus(
    bool Present,
    bool HeaderCrcValid,
    bool? PartitionEntryArrayValid,
    string? Error = null)
{
    public bool IsUsable =>
        Present && HeaderCrcValid && PartitionEntryArrayValid is not false && Error is null;
}