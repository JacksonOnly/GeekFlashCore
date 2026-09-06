namespace GeekFlashCore.BlockDevice.Abstractions;

public enum BlockExtentKind
{
    Linear,
    Zero
}

public readonly record struct BlockDeviceExtent
{
    public BlockDeviceExtent(
        long logicalOffset,
        long length,
        BlockExtentKind kind,
        long physicalOffset = 0,
        int sourceIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logicalOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(physicalOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceIndex);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == BlockExtentKind.Zero && (physicalOffset != 0 || sourceIndex != 0))
            throw new ArgumentOutOfRangeException(nameof(physicalOffset));

        LogicalOffset = logicalOffset;
        Length = length;
        Kind = kind;
        PhysicalOffset = physicalOffset;
        SourceIndex = sourceIndex;
    }

    public long LogicalOffset { get; }
    public long Length { get; }
    public BlockExtentKind Kind { get; }
    public long PhysicalOffset { get; }
    public int SourceIndex { get; }
}
