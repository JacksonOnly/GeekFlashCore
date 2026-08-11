namespace GeekFlashCore.FileSystem.Erofs.Internals;

internal sealed class ErofsRangeStream : Stream
{
    private readonly ErofsVolume _volume;
    private readonly long _baseOffset;
    private readonly long _length;
    private readonly string _structure;
    private readonly string _objectId;
    private long _position;
    private bool _disposed;

    public ErofsRangeStream(
        ErofsVolume volume,
        long baseOffset,
        long length,
        string structure,
        string objectId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _volume = volume;
        _baseOffset = baseOffset;
        _length = length;
        _structure = structure;
        _objectId = objectId;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set
        {
            ThrowIfDisposed();
            if (value < 0 || value > _length)
                throw new IOException(Strings.PositionOutsideValue);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        int count = (int)Math.Min(buffer.Length, _length - _position);
        if (count == 0) return 0;
        _volume.ReadExactlyAt(
            checked(_baseOffset + _position),
            buffer[..count],
            _structure,
            _objectId);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = position;
        return position;
    }

    public override void Flush() => ThrowIfDisposed();
    public override void SetLength(long value) => throw new NotSupportedException(Strings.ReadOnlyStream);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Strings.ReadOnlyStream);

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volume.ThrowIfDisposed();
    }
}
