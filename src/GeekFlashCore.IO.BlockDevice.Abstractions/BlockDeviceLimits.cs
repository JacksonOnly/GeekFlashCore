namespace GeekFlashCore.IO.BlockDevice.Abstractions;

public sealed record BlockDeviceLimits
{
    public const int DefaultMaxReadAheadSize = 1024 * 1024;
    public const int AbsoluteMaxReadAheadSize = 16 * 1024 * 1024;

    public static BlockDeviceLimits Default { get; } = new();

    public int MaxReadAheadSize { get; init; } = DefaultMaxReadAheadSize;

    public void Validate()
    {
        if (MaxReadAheadSize is < 1 or > AbsoluteMaxReadAheadSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxReadAheadSize),
                MaxReadAheadSize,
                Strings.FormatExpectedRange(1, AbsoluteMaxReadAheadSize));
        }
    }

    public int ValidateReadAheadSize(int requestedSize, int logicalBlockSize)
    {
        Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalBlockSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedSize, logicalBlockSize);

        long alignedSize = checked(
            (checked((long)requestedSize + logicalBlockSize - 1) / logicalBlockSize) * logicalBlockSize);
        if (alignedSize > MaxReadAheadSize || alignedSize > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedSize),
                requestedSize,
                Strings.FormatAlignedReadAheadSizeExceeded(MaxReadAheadSize));
        }

        return (int)alignedSize;
    }
}
