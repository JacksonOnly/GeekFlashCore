using System.Buffers.Binary;
using GeekFlashCore.Android.Sparse.Types;

namespace GeekFlashCore.Android.Sparse.Internals;

internal sealed class SparseCompositionStream(SparseImageComposition image, CompositionSource[] sources,
    CompositionChunk[] chunks, int maximumOpenSources, CancellationToken ct, CancellationTokenSource? linked) : Stream
{
    private readonly Dictionary<int, (Stream Stream, long Access)> _open = new();
    private long _cursor, _access;
    private bool _disposed;
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    private void Check() { ObjectDisposedException.ThrowIf(_disposed, this); ct.ThrowIfCancellationRequested(); }
    public override long Length { get { Check(); return image.EncodedLength; } }
    public override long Position { get { Check(); return _cursor; } set => Seek(value, SeekOrigin.Begin); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Check(); long next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_cursor + offset), SeekOrigin.End => checked(Length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (next < 0 || next > Length) throw new IOException(Strings.CompositionSeekInvalid);
        _cursor = next; return next;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        Check(); if (buffer.IsEmpty || _cursor == Length) return 0;
        Span<byte> metadata = stackalloc byte[28]; metadata.Clear();
        if (_cursor < 28)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(metadata, 0xed26ff3a); BinaryPrimitives.WriteUInt16LittleEndian(metadata[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(metadata[8..], 28); BinaryPrimitives.WriteUInt16LittleEndian(metadata[10..], 12);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata[12..], image.BlockSize); BinaryPrimitives.WriteUInt32LittleEndian(metadata[16..], image.TotalBlocks);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata[20..], checked((uint)chunks.Length));
            return Copy(metadata, (int)_cursor, buffer);
        }
        int low = 0, high = chunks.Length - 1;
        while (low < high) { int mid = low + (high - low + 1) / 2; if (chunks[mid].EncodedOffset <= _cursor) low = mid; else high = mid - 1; }
        var chunk = chunks[low]; long relative = _cursor - chunk.EncodedOffset;
        long payload = chunk.Type == SparseChunkType.Raw ? checked((long)chunk.BlockCount * image.BlockSize) : chunk.Type == SparseChunkType.Fill ? 4 : 0;
        if (relative < 12)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(metadata, (ushort)chunk.Type); BinaryPrimitives.WriteUInt32LittleEndian(metadata[4..], chunk.BlockCount);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata[8..], checked((uint)(12 + payload))); return Copy(metadata[..12], (int)relative, buffer);
        }
        if (chunk.Type == SparseChunkType.Fill)
        { BinaryPrimitives.WriteUInt32LittleEndian(metadata, chunk.FillValue); return Copy(metadata[..4], (int)(relative - 12), buffer); }
        Stream input = Source(chunk.SourceIndex); input.Position = checked(chunk.PayloadOffset + relative - 12);
        int count = (int)Math.Min(Math.Min(buffer.Length, 65536), payload - (relative - 12)); int n = input.Read(buffer[..count]);
        Check(); if (n == 0) throw new SparseException(Strings.RawPayloadTruncated); _cursor += n; return n;
    }
    private int Copy(ReadOnlySpan<byte> source, int offset, Span<byte> destination)
    { int count = Math.Min(destination.Length, source.Length - offset); source.Slice(offset, count).CopyTo(destination); _cursor += count; return count; }
    private Stream Source(int index)
    {
        if (_open.TryGetValue(index, out var cached)) { _open[index] = (cached.Stream, ++_access); return cached.Stream; }
        if (_open.Count >= maximumOpenSources)
        { int oldest = _open.MinBy(p => p.Value.Access).Key; var closing = _open[oldest].Stream; _open.Remove(oldest); closing.Dispose(); }
        Stream? source = null;
        try
        {
            source = sources[index].Open(ct); SparseImageComposer.ValidateSource(source, 0);
            if (source.Length != sources[index].Length) throw new SparseException(Strings.CompositionSourceInvalid);
            source.Position = 0; Check(); _open.Add(index, (source, ++_access)); return source;
        }
        catch { source?.Dispose(); throw; }
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => Check();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Strings.StreamOperationNotSupported);
    public override void SetLength(long value) => throw new NotSupportedException(Strings.StreamOperationNotSupported);
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return; _disposed = true;
        try
        {
            if (disposing)
            {
                Exception? failure = null;
                foreach (var item in _open.Values) { try { item.Stream.Dispose(); } catch (Exception e) { failure ??= e; } }
                _open.Clear(); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally { linked?.Dispose(); base.Dispose(disposing); }
    }
}
