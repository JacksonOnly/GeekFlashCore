namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record FirehoseReadRequest
{
    public FirehoseStorage? Storage { get; init; }
    public uint? Slot { get; init; }
    public uint PhysicalPartitionNumber { get; init; }
    public long StartSector { get; init; }
    public long SectorCount { get; init; }
    public uint SectorSizeInBytes { get; init; } = 512;
    public string? Label { get; init; }
    public string? FileName { get; init; }
    public FirehoseIoOptions IoOptions { get; init; } = new();

    public long GetByteLength()
    {
        Validate();
        return checked(SectorCount * SectorSizeInBytes);
    }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(IoOptions);
        if (PhysicalPartitionNumber >= FirehoseConstants.MaximumPhysicalPartitionCount)
            throw new ArgumentOutOfRangeException(nameof(PhysicalPartitionNumber));
        if (StartSector < 0)
            throw new ArgumentOutOfRangeException(nameof(StartSector));
        if (SectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(SectorCount));
        if (SectorSizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(SectorSizeInBytes));
        _ = checked(SectorCount * SectorSizeInBytes);
    }
}
