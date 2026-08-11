using GeekFlashCore.Android.Sparse.Constants;
using GeekFlashCore.BlockDevice;

namespace GeekFlashCore.Android.Sparse.Models;

public sealed record SparseImageWriteOptions
{
    public const int DefaultBlockSize = 4096;
    public const int DefaultBufferSize = BoundedStreamCopier.DefaultBufferSize;
    public const long DefaultMaxRawChunkSize = 64L * 1024 * 1024;

    public int BlockSize { get; init; } = DefaultBlockSize;
    public int BufferSize { get; init; } = DefaultBufferSize;
    public long MaxRawChunkSize { get; init; } = DefaultMaxRawChunkSize;
    public int MaxChunkCount { get; init; } = ushort.MaxValue;
    public bool DetectFillChunks { get; init; } = true;
    public bool UseDontCareForZeroBlocks { get; init; } = true;
    public bool IncludeCrc32Chunk { get; init; }

    internal void Validate()
    {
        if (BlockSize < sizeof(uint) || (BlockSize & (sizeof(uint) - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(BlockSize));

        if (BufferSize < sizeof(uint) || BufferSize > BoundedStreamCopier.MaximumBufferSize ||
            (BufferSize & (sizeof(uint) - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(BufferSize));

        if (MaxRawChunkSize < BlockSize ||
            MaxRawChunkSize > uint.MaxValue - SparseConstant.ChunkLength ||
            MaxRawChunkSize % BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRawChunkSize));

        if (MaxChunkCount is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxChunkCount));
    }

    internal int MaxRawChunkBlocks => checked((int)Math.Min(
        MaxRawChunkSize / BlockSize,
        uint.MaxValue));
}
