namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptCrcStatus(bool HeaderValid, bool? PartitionEntryArrayValid)
{
    public bool FullyValid => HeaderValid && PartitionEntryArrayValid is true;
}
