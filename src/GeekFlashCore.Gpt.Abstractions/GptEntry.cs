namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptEntry(
    int Number,
    int SlotIndex,
    Guid TypeId,
    Guid Id,
    ulong FirstLba,
    ulong LastLba,
    ulong Attributes,
    string Name)
{
    public ulong SectorCount => LastLba < FirstLba ? 0 : checked(LastLba - FirstLba + 1);
}
