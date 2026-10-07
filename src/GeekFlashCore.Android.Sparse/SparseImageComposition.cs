using GeekFlashCore.Android.Sparse.Internals;
using GeekFlashCore.Android.Sparse.Types;

namespace GeekFlashCore.Android.Sparse;

/// <summary>An immutable sparse encoding that references the original sources without expanding them.</summary>
/// <remarks>Sources are borrowed. Each output stream owns its opened source streams; dispose each stream.
/// Later RAW/FILL data overrides earlier data; DONT_CARE never overrides data.</remarks>
public sealed class SparseImageComposition
{
    private readonly CompositionSource[] _sources;
    private readonly CompositionChunk[] _chunks;
    private readonly int _maximumOpenSources;
    private readonly CancellationToken _cancellation;

    internal SparseImageComposition(CompositionSource[] sources, CompositionChunk[] chunks, uint blockSize,
        uint totalBlocks, long encodedLength, int maximumOpenSources, CancellationToken cancellation)
    {
        _sources = sources; _chunks = chunks; BlockSize = blockSize; TotalBlocks = totalBlocks;
        EncodedLength = encodedLength; _maximumOpenSources = maximumOpenSources; _cancellation = cancellation;
    }
    /// <summary>Encoded stream length, including generated sparse headers.</summary>
    public long EncodedLength { get; }
    /// <summary>Logical image length; no buffer of this size is allocated.</summary>
    public long ExpandedLength => checked((long)BlockSize * TotalBlocks);
    /// <summary>Logical block size shared by all sources.</summary>
    public uint BlockSize { get; }
    /// <summary>Logical block count shared by all sources.</summary>
    public uint TotalBlocks { get; }
    /// <summary>Number of generated sparse chunks, including DONT_CARE gaps.</summary>
    public int ChunkCount => _chunks.Length;
    /// <summary>Opens an independent readable, seekable encoded sparse stream.</summary>
    public Stream OpenStream(CancellationToken cancellationToken = default)
    {
        _cancellation.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? linked = null;
        CancellationToken effective = _cancellation.CanBeCanceled ? _cancellation : cancellationToken;
        if (_cancellation.CanBeCanceled && cancellationToken.CanBeCanceled && _cancellation != cancellationToken)
        { linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, cancellationToken); effective = linked.Token; }
        try { return new SparseCompositionStream(this, _sources, _chunks, _maximumOpenSources, effective, linked); }
        catch { linked?.Dispose(); throw; }
    }
}

internal readonly record struct CompositionSource(Func<CancellationToken, Stream> Open, long Length);
internal readonly record struct CompositionChunk(long EncodedOffset, uint StartBlock, uint BlockCount,
    SparseChunkType Type, int SourceIndex, long PayloadOffset, uint FillValue);
