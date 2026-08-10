namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptCreationOptions
{
    public ulong TotalDiskSectors { get; init; }
    public int SectorSize { get; init; } = 512;
    public int PartitionEntryCount { get; init; } = 128;
    public int PartitionEntrySize { get; init; } = 128;
    public Guid? DiskId { get; init; }
    public bool IncludeUnallocatedRegions { get; init; }
}
