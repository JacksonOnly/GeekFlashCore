using System.Collections.ObjectModel;
using GeekFlashCore.Android.Sparse.Types;

namespace GeekFlashCore.Android.Sparse.Models;

public sealed class SparseImageWritePlan
{
    private readonly SparseImageWriteChunk[] _chunks;
    private readonly ReadOnlyCollection<SparseImageWriteChunk> _readOnlyChunks;

    internal SparseImageWritePlan(
        long sourceLength,
        long rawLength,
        uint blockSize,
        uint totalBlocks,
        SparseImageWriteChunk[] chunks,
        long encodedLength,
        bool includesCrc32Chunk,
        uint checksum)
    {
        SourceLength = sourceLength;
        RawLength = rawLength;
        BlockSize = blockSize;
        TotalBlocks = totalBlocks;
        _chunks = chunks;
        _readOnlyChunks = Array.AsReadOnly(chunks);
        EncodedLength = encodedLength;
        IncludesCrc32Chunk = includesCrc32Chunk;
        Checksum = includesCrc32Chunk ? checksum : null;
    }

    public long SourceLength { get; }
    public long RawLength { get; }
    public uint BlockSize { get; }
    public uint TotalBlocks { get; }
    public uint ChunkCount => checked((uint)(_chunks.Length + (IncludesCrc32Chunk ? 1 : 0)));
    public long EncodedLength { get; }
    public bool IncludesCrc32Chunk { get; }
    public uint? Checksum { get; }
    public IReadOnlyList<SparseImageWriteChunk> Chunks => _readOnlyChunks;

    internal ReadOnlySpan<SparseImageWriteChunk> ChunkSpan => _chunks;
    internal SparseImageWriteChunk[] ChunkArray => _chunks;
}
