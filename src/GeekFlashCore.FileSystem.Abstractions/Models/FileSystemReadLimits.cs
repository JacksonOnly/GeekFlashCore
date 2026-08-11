namespace GeekFlashCore.FileSystem.Abstractions;

public sealed record FileSystemReadLimits
{
    public const int AbsoluteMaximumCacheBytes = 64 * 1024 * 1024;
    public const int AbsoluteMaximumWorkingBytes = 64 * 1024 * 1024;
    public const int AbsoluteMaximumCompressedInputBytes = 1024 * 1024;
    public const int AbsoluteMaximumDecodedBytes = 12 * 1024 * 1024;

    public static FileSystemReadLimits Default { get; } = new();

    public FileSystemReadLimits(
        int maximumCacheBytes = 8 * 1024 * 1024,
        int maximumWorkingBytes = 16 * 1024 * 1024,
        int maximumCompressedInputBytes = 1024 * 1024,
        int maximumDecodedBytes = 1024 * 1024,
        int maximumSymlinkBytes = 1024 * 1024,
        int maximumMappingDepth = 16)
    {
        Validate(maximumCacheBytes, 1, AbsoluteMaximumCacheBytes, nameof(maximumCacheBytes));
        Validate(maximumWorkingBytes, 4096, AbsoluteMaximumWorkingBytes, nameof(maximumWorkingBytes));
        Validate(
            maximumCompressedInputBytes,
            1,
            AbsoluteMaximumCompressedInputBytes,
            nameof(maximumCompressedInputBytes));
        Validate(maximumDecodedBytes, 1, AbsoluteMaximumDecodedBytes, nameof(maximumDecodedBytes));
        Validate(maximumSymlinkBytes, 1, 16 * 1024 * 1024, nameof(maximumSymlinkBytes));
        Validate(maximumMappingDepth, 1, 64, nameof(maximumMappingDepth));
        if ((long)maximumCompressedInputBytes + maximumDecodedBytes > maximumWorkingBytes)
        {
            throw new ArgumentException(
                nameof(maximumWorkingBytes));
        }

        MaximumCacheBytes = maximumCacheBytes;
        MaximumWorkingBytes = maximumWorkingBytes;
        MaximumCompressedInputBytes = maximumCompressedInputBytes;
        MaximumDecodedBytes = maximumDecodedBytes;
        MaximumSymlinkBytes = maximumSymlinkBytes;
        MaximumMappingDepth = maximumMappingDepth;
    }

    public int MaximumCacheBytes { get; }
    public int MaximumWorkingBytes { get; }
    public int MaximumCompressedInputBytes { get; }
    public int MaximumDecodedBytes { get; }
    public int MaximumSymlinkBytes { get; }
    public int MaximumMappingDepth { get; }

    private static void Validate(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName);
        }
    }
}