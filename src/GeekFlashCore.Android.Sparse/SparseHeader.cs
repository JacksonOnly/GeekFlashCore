namespace GeekFlashCore.Android.Sparse;

public readonly record struct SparseHeader(
    ushort MajorVersion,
    ushort MinorVersion,
    ushort FileHeaderSize,
    ushort ChunkHeaderSize,
    uint BlockSize,
    uint TotalBlocks,
    uint TotalChunks,
    uint ImageChecksum)
{
    public long RawLength => checked((long)BlockSize * TotalBlocks);
}