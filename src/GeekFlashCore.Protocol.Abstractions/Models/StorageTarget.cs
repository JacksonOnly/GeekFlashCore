namespace GeekFlashCore.Protocol.Abstractions;

public abstract record StorageTarget
{
    public uint? PhysicalPartitionNumber { get; init; }
}

public record PartitionTarget : StorageTarget
{
    public string Name { get; init; } = string.Empty;
}

public record SectorTarget : StorageTarget
{
    public long StartSector { get; init; }
    public long SectorCount { get; init; }
    public long SectorSize { get; init; } = 512; 
}

public record OffsetTarget : StorageTarget
{
    public long StartOffset { get; init; }
    public long Length { get; init; }
}
