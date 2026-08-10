namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptEntryOverlap(
    int FirstPartitionNumber,
    int SecondPartitionNumber,
    ulong FirstLba,
    ulong LastLba)
{
    public ulong SectorCount => checked(LastLba - FirstLba + 1);
}
