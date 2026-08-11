namespace GeekFlashCore.FileSystem.Erofs.Internals;

internal sealed class ErofsSubstream : Stream
{
    private readonly Stream _source;
    private readonly long _baseOffset;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    public ErofsSubstream(Stream source, long baseOffset, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException(Strings.SourceMustBeReadableSeekable, nameof(source));
        if (baseOffset > source.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length));
        _source = source;
        _baseOffset = baseOffset;
        _length = length;
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
                throw new IOException(Strings.PositionOutsideSubstream);
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
        _source.Position = checked(_baseOffset + _position);
        int read = _source.Read(buffer[..count]);
        _position += read;
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
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
        if (_disposed) return;
        _disposed = true;
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
