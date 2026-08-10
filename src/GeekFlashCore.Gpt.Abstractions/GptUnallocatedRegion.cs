namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptUnallocatedRegion(ulong FirstLba, ulong LastLba)
{
    public ulong SectorCount => checked(LastLba - FirstLba + 1);
}
