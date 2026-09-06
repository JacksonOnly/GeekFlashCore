namespace GeekFlashCore.ImageFormats.Abstractions;

public sealed record ImageReadLimits
{
    public const int AbsoluteMaxSparseChunkCount = 262_144;
    public const int AbsoluteMaxSparseChunkIndexBytes = 8 * 1024 * 1024;
    public const int AbsoluteMaxLpMetadataSlots = 8;
    public const int AbsoluteMaxLpMetadataBytes = 64 * 1024 * 1024;
    public const int AbsoluteMaxLpTableEntries = 1_048_576;
    public const int AbsoluteMaxDecodedLpMetadataBytes = 64 * 1024 * 1024;

    public static ImageReadLimits Default { get; } = new();

    public int MaxSparseChunkCount { get; init; } = AbsoluteMaxSparseChunkCount;
    public int MaxSparseChunkIndexBytes { get; init; } = AbsoluteMaxSparseChunkIndexBytes;
    public int MaxLpMetadataSlots { get; init; } = AbsoluteMaxLpMetadataSlots;
    public int MaxLpMetadataBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxLpTableEntries { get; init; } = 262_144;
    public int MaxDecodedLpMetadataBytes { get; init; } = 16 * 1024 * 1024;
    public int ChecksumBufferSize { get; init; } = 64 * 1024;

    public void Validate()
    {
        ValidateRange(MaxSparseChunkCount, 1, AbsoluteMaxSparseChunkCount, nameof(MaxSparseChunkCount));
        ValidateRange(MaxSparseChunkIndexBytes, 1, AbsoluteMaxSparseChunkIndexBytes, nameof(MaxSparseChunkIndexBytes));
        ValidateRange(MaxLpMetadataSlots, 1, AbsoluteMaxLpMetadataSlots, nameof(MaxLpMetadataSlots));
        ValidateRange(MaxLpMetadataBytes, 1, AbsoluteMaxLpMetadataBytes, nameof(MaxLpMetadataBytes));
        ValidateRange(MaxLpTableEntries, 1, AbsoluteMaxLpTableEntries, nameof(MaxLpTableEntries));
        ValidateRange(MaxDecodedLpMetadataBytes, 1, AbsoluteMaxDecodedLpMetadataBytes, nameof(MaxDecodedLpMetadataBytes));
        ValidateRange(ChecksumBufferSize, 4 * 1024, 64 * 1024, nameof(ChecksumBufferSize));

        if (MaxDecodedLpMetadataBytes < MaxLpMetadataBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDecodedLpMetadataBytes));
        }
    }

    private static void ValidateRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                Resources.FormatExpectedRange(minimum, maximum));
        }
    }
}
