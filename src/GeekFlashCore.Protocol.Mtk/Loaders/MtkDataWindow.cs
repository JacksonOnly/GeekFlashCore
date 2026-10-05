using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Loaders;

/// <summary>Reopenable seekable source window; only streams opened by this object are disposed.</summary>
public sealed class MtkDataWindow : IDataSource
{
    private readonly IDataSource _source;
    private readonly long _offset;
    public MtkDataWindow(IDataSource source, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (offset < 0 || length <= 0 || offset > source.Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        _source = source;
        _offset = offset;
        Length = length;
    }
    public long Length
    {
        get;
    }
    public Stream OpenStream()
    {
        Stream stream = _source.OpenStream();
        try
        {
            if (!stream.CanRead || !stream.CanSeek || _offset > stream.Length - Length)
                throw new MtkResourceException("DA window");
            stream.Position = _offset;
            return new WindowStream(stream, Length);
        }
        catch { stream.Dispose(); throw; }
    }
    public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(OpenStream());
    }

    private sealed class WindowStream(Stream source, long length) : Stream
    {
        private readonly long _origin = source.Position;
        private long _position;
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position; set => Seek(value, SeekOrigin.Begin);
        }
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - _position);
            int read = source.Read(buffer[..count]);
            _position += read;
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!Enum.IsDefined(origin))
                throw new ArgumentOutOfRangeException(nameof(origin));
            long pos = checked(offset + (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? _position : length));
            if (pos < 0 || pos > length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            source.Position = checked(_origin + pos);
            return _position = pos;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                source.Dispose();
            base.Dispose(disposing);
        }
        public override void Flush()
        {
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
