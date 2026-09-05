using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record FirehoseProgramRequest
{
    public required IDataSource Source { get; init; }
    public FirehoseStorage? Storage { get; init; }
    public uint? Slot { get; init; }
    public uint PhysicalPartitionNumber { get; init; }
    public long StartSector { get; init; }
    public long SectorCount { get; init; }
    public uint SectorSizeInBytes { get; init; } = 512;
    public long SourceOffset { get; init; }
    public long? SourceLength { get; init; }
    public FirehoseProgramFormat Format { get; init; } = FirehoseProgramFormat.Auto;
    public string? Label { get; init; }
    public string? FileName { get; init; }
    public byte PaddingByte { get; init; }
    public FirehoseIoOptions IoOptions { get; init; } = new();

    public long GetWireLength()
    {
        Validate();
        return checked(SectorCount * SectorSizeInBytes);
    }

    public long GetSourceLength()
    {
        Validate();
        return SourceLength ?? checked(Source.Length - SourceOffset);
    }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Source);
        ArgumentNullException.ThrowIfNull(IoOptions);
        if (PhysicalPartitionNumber >= FirehoseConstants.MaximumPhysicalPartitionCount)
            throw new ArgumentOutOfRangeException(nameof(PhysicalPartitionNumber));
        if (StartSector < 0)
            throw new ArgumentOutOfRangeException(nameof(StartSector));
        if (SectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(SectorCount));
        if (SectorSizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(SectorSizeInBytes));
        if (SourceOffset < 0 || SourceOffset > Source.Length)
            throw new ArgumentOutOfRangeException(nameof(SourceOffset));
        if (SourceLength is < 0)
            throw new ArgumentOutOfRangeException(nameof(SourceLength));

        long sourceLength = SourceLength ?? checked(Source.Length - SourceOffset);
        if (sourceLength > Source.Length - SourceOffset)
            throw new ArgumentOutOfRangeException(nameof(SourceLength));
        long wireLength = checked(SectorCount * SectorSizeInBytes);
        if (Format != FirehoseProgramFormat.AndroidSparse && sourceLength > wireLength)
            throw new ArgumentException(Strings.ProgramSourceLargerThanTarget, nameof(SourceLength));
    }
}
