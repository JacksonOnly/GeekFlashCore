using GeekFlashCore.Android.Sparse.Internals;

namespace GeekFlashCore.Android.Sparse.Models;

public sealed class SparseRegion
{
    private readonly IReadOnlyList<SparseDataChunk> _chunks;

    internal SparseRegion(uint startBlock, long length, IReadOnlyList<SparseDataChunk> chunks)
    {
        StartBlock = startBlock;
        Length = length;
        _chunks = chunks;
    }

    public uint StartBlock { get; }
    public long Length { get; }

    public Stream OpenRead(Stream source, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException(Strings.SourceMustBeReadableAndSeekable, nameof(source));

        return new SparseRegionStream(source, _chunks, Length, leaveOpen);
    }
}