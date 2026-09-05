namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

internal sealed class SourceWindowStream : Stream
{
    private readonly long _length;
    private readonly long _origin;
    private readonly Stream _source;
    private long _position;
    private bool _disposed;

    public SourceWindowStream(Stream source, long origin, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException(Strings.Qcom_SourceWindowRequiresSeekable, nameof(source));
        if (origin < 0 || length < 0 || origin > source.Length - length)
            throw new ArgumentOutOfRangeException(nameof(origin));

        _source = source;
        _origin = origin;
        _length = length;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int count = (int)Math.Min(buffer.Length, _length - _position);
        if (count == 0)
            return 0;
        long absolutePosition = checked(_origin + _position);
        if (_source.Position != absolutePosition)
            _source.Position = absolutePosition;
        int read = _source.Read(buffer[..count]);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if ((ulong)position > (ulong)_length)
            throw new IOException(Strings.Qcom_SourceWindowSeekOutside);
        _position = position;
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
