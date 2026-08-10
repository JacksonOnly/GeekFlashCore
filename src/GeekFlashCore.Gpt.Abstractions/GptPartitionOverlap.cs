namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptPartitionOverlap(
    int FirstPartitionNumber,
    int SecondPartitionNumber,
    ulong FirstLba,
    ulong LastLba)
{
    public ulong SectorCount => checked(LastLba - FirstLba + 1);
}
