using System.Buffers.Binary;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.Android.Sparse.Types;

namespace GeekFlashCore.Android.Sparse.Internals;


internal sealed class SparseRegionStream : Stream
{
    private readonly Stream _source;
    private readonly IReadOnlyList<SparseDataChunk> _chunks;
    private readonly bool _leaveOpen;
    private int _chunkIndex;
    private long _chunkPosition;
    private long _position;
    private bool _disposed;

    public SparseRegionStream(
        Stream source,
        IReadOnlyList<SparseDataChunk> chunks,
        long length,
        bool leaveOpen)
    {
        _source = source;
        _chunks = chunks;
        Length = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; }
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException(Strings.StreamOperationNotSupported);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int written = 0;

        while (!buffer.IsEmpty && _chunkIndex < _chunks.Count)
        {
            SparseDataChunk chunk = _chunks[_chunkIndex];
            int count = (int)Math.Min(buffer.Length, chunk.Length - _chunkPosition);
            Span<byte> destination = buffer[..count];

            if (chunk.Type == SparseDataChunkType.Raw)
            {
                long sourcePosition = checked(chunk.SourceOffset + _chunkPosition);
                if (_source.Position != sourcePosition)
                    _source.Position = sourcePosition;
                _source.ReadExactly(destination);
            }
            else
            {
                SparsePattern.Fill(destination, chunk.FillPattern, _chunkPosition);
            }

            written += count;
            _position += count;
            _chunkPosition += count;
            buffer = buffer[count..];

            if (_chunkPosition == chunk.Length)
            {
                _chunkIndex++;
                _chunkPosition = 0;
            }
        }

        return written;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(Strings.StreamOperationNotSupported);
    public override void SetLength(long value) => throw new NotSupportedException(Strings.StreamOperationNotSupported);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Strings.StreamOperationNotSupported);

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing && !_leaveOpen)
            _source.Dispose();
        _disposed = true;
        base.Dispose(disposing);
    }
}
