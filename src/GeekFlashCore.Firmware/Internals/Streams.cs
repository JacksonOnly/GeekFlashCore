using System.Buffers;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal abstract class ReadOnlyStream(CancellationToken cancellation = default) : Stream
{
    private bool _disposed;
    protected long Cursor;
    protected CancellationToken Cancellation => cancellation;
    protected virtual void Check() { ObjectDisposedException.ThrowIf(_disposed, this); cancellation.ThrowIfCancellationRequested(); }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Position { get { Check(); return Cursor; } set => Seek(value, SeekOrigin.Begin); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Check(); long position = origin switch
        { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(Cursor + offset), SeekOrigin.End => checked(Length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (position < 0 || position > Length) throw new IOException(Strings.InvalidSeek);
        Cursor = position; return position;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => Check();
    public override void SetLength(long value) => throw new NotSupportedException(Strings.ReadOnly);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Strings.ReadOnly);
    protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
}

internal sealed class PackageStream(FirmwarePackage owner, Stream inner, CancellationToken ct, CancellationTokenSource? linked) : ReadOnlyStream(ct)
{
    protected override void Check() { base.Check(); owner.Check(); }
    public override long Length { get { Check(); return inner.Length; } }
    public override int Read(Span<byte> buffer) { Check(); int n = inner.Read(buffer); Cursor = inner.Position; return n; }
    public override long Seek(long offset, SeekOrigin origin) { Check(); Cursor = inner.Seek(offset, origin); return Cursor; }
    protected override void Dispose(bool disposing)
    {
        try { if (disposing && CanRead) { try { inner.Dispose(); } finally { linked?.Dispose(); } } }
        finally { base.Dispose(disposing); }
    }
}

internal static class SourceStream
{
    internal static Stream Open(IDataSource source)
    {
        Stream stream = source.OpenStream();
        try
        {
            if (!stream.CanRead || !stream.CanSeek || stream.Length != source.Length) throw new InvalidDataException(Strings.InvalidSource);
            stream.Position = 0; return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    internal static void Range(long total, long offset, long length)
    { if (total < 0 || offset < 0 || length < 0 || offset > total || length > total - offset) throw new InvalidDataException(Strings.InvalidRange); }
    internal static Stream Slice(IDataSource source, long offset, long length, CancellationToken ct = default)
    { Range(source.Length, offset, length); ct.ThrowIfCancellationRequested(); return new SliceStream(Open(source), offset, length, ct); }
}

internal sealed class SliceStream(Stream source, long offset, long length, CancellationToken ct = default) : ReadOnlyStream(ct)
{
    public override long Length { get { Check(); return length; } }
    public override int Read(Span<byte> buffer)
    {
        Check(); int count = (int)Math.Min(buffer.Length, length - Cursor);
        if (count == 0) return 0;
        source.Position = checked(offset + Cursor); int n = source.Read(buffer[..count]);
        if (n == 0) throw new InvalidDataException(Strings.Truncated);
        Cursor += n; return n;
    }
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
}

internal sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
    public override int Read(Span<byte> b) => inner.Read(b);
    public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
    public override void Flush() { }
    public override void SetLength(long v) => throw new NotSupportedException(Strings.ReadOnly);
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException(Strings.ReadOnly);
    protected override void Dispose(bool disposing)
    { if (disposing) { try { inner.Dispose(); } finally { owner.Dispose(); } } base.Dispose(disposing); }
}

// Some codecs require complete header reads. Fill each requested span while preserving finite EOF.
internal sealed class DecoderInputStream(Stream inner, CancellationToken ct) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { ct.ThrowIfCancellationRequested(); inner.Position = value; } }
    public override int Read(byte[] b, int offset, int count) => Read(b.AsSpan(offset, count));
    public override int Read(Span<byte> b)
    {
        int read = 0;
        while (read < b.Length) { ct.ThrowIfCancellationRequested(); int n = inner.Read(b[read..]); if (n == 0) break; read += n; }
        return read;
    }
    public override int ReadByte() { ct.ThrowIfCancellationRequested(); return inner.ReadByte(); }
    public override long Seek(long offset, SeekOrigin origin) { ct.ThrowIfCancellationRequested(); return inner.Seek(offset, origin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException(Strings.ReadOnly);
    public override void Write(byte[] b, int offset, int count) => throw new NotSupportedException(Strings.ReadOnly);
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}

// Seek uses replay, never a temporary file or a growing in-memory cache.
internal sealed class ReplayStream(Func<Stream> factory, long length, int bufferSize, CancellationToken ct, long maximumPadding = 0) : ReadOnlyStream(ct)
{
    private Stream? _decoder;
    private long _decoded;
    private bool _ended;
    public override long Length { get { Check(); return length; } }
    private void Align()
    {
        if (_decoder is null || _decoded > Cursor)
        { _decoder?.Dispose(); _decoder = null; _decoder = factory(); _decoded = 0; _ended = false; }
        if (_decoded == Cursor) return;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (_decoded < Cursor)
            {
                Check(); int size = (int)Math.Min(bufferSize, Cursor - _decoded);
                int n = Decode(scratch.AsSpan(0, size)); _decoded += n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(scratch, clearArray: true); }
    }
    private int Decode(Span<byte> buffer)
    {
        int n = _ended ? 0 : _decoder!.Read(buffer);
        if (n == 0)
        {
            _ended = true;
            if (length - _decoded > maximumPadding) throw new InvalidDataException(Strings.Truncated);
            buffer.Clear(); return buffer.Length;
        }
        return n;
    }
    public override int Read(Span<byte> buffer)
    {
        Check(); if (buffer.Length == 0) return 0; Align();
        int count = (int)Math.Min(buffer.Length, length - Cursor);
        if (count == 0) { if (!_ended && _decoder!.ReadByte() != -1) throw new InvalidDataException(Strings.LengthMismatch); _ended = true; return 0; }
        int n = Decode(buffer[..count]); Cursor += n; _decoded += n;
        if (Cursor == length) { if (!_ended && _decoder!.ReadByte() != -1) throw new InvalidDataException(Strings.LengthMismatch); _ended = true; }
        return n;
    }
    protected override void Dispose(bool disposing) { if (disposing) _decoder?.Dispose(); base.Dispose(disposing); }
}
