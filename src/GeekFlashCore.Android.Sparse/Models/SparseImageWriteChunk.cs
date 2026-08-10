using GeekFlashCore.Android.Sparse.Types;

namespace GeekFlashCore.Android.Sparse.Models;


public readonly record struct SparseImageWriteChunk
{
    internal SparseImageWriteChunk(
        SparseChunkType type,
        uint startBlock,
        uint blockCount,
        uint fillValue)
    {
        Type = type;
        StartBlock = startBlock;
        BlockCount = blockCount;
        FillValue = fillValue;
    }

    public SparseChunkType Type { get; }
    public uint StartBlock { get; }
    public uint BlockCount { get; }
    public uint FillValue { get; }
}
