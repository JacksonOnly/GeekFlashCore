using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using GeekFlashCore.Android.Sparse.Internals;
using GeekFlashCore.Android.Sparse.Types;

namespace GeekFlashCore.Android.Sparse;

/// <summary>A bounded forward reader for streaming Sparse directly to a positioned raw writer.</summary>
/// <remarks>No payload pre-scan or chunk index is created. Later corruption can be discovered after earlier
/// callbacks have completed. The source is borrowed; callback streams expire when the callback returns.</remarks>
public static class SparseSequentialReader
{
    /// <summary>Validates only the fixed 28-byte header and leaves the source immediately after it.</summary>
    public static SparseHeader ReadHeader(Stream source, int maximumChunks = 262144, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek || source.Position != 0 || source.Length < 28 || maximumChunks < 1)
            throw new SparseException(Strings.CompositionSourceInvalid);
        Span<byte> bytes = stackalloc byte[28]; ReadExactly(source, bytes, cancellationToken);
        return ParseHeader(source.Length, bytes, maximumChunks);
    }
    /// <summary>Consumes four bytes for Raw or the fixed Sparse header, without rewinding a compressed source.</summary>
    /// <returns>The validated Sparse header, or null when the source is Raw.</returns>
    public static SparseHeader? TryReadHeader(Stream source, int maximumChunks = 262144, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek || source.Position != 0 || source.Length < 4 || maximumChunks < 1)
            throw new SparseException(Strings.CompositionSourceInvalid);
        Span<byte> bytes = stackalloc byte[28]; ReadExactly(source, bytes[..4], cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0xed26ff3a) return null;
        ReadExactly(source, bytes[4..], cancellationToken); return ParseHeader(source.Length, bytes, maximumChunks);
    }
    private static SparseHeader ParseHeader(long length, ReadOnlySpan<byte> bytes, int maximumChunks)
    {
        var h = new SparseHeader(BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]));
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0xed26ff3a || h.MajorVersion != 1 || h.FileHeaderSize < 28 ||
            h.ChunkHeaderSize < 12 || h.BlockSize == 0 || h.BlockSize > int.MaxValue || h.BlockSize % 4 != 0 ||
            h.TotalBlocks == 0 || h.TotalChunks == 0 || h.FileHeaderSize > length ||
            h.TotalChunks > (ulong)((length - h.FileHeaderSize) / h.ChunkHeaderSize))
            throw new SparseException(Strings.CompositionSourceInvalid);
        if (h.TotalChunks > maximumChunks) throw new SparseException(Strings.CompositionLimitExceeded);
        return h;
    }

    /// <summary>Consumes each chunk once, dispatching only Raw/Fill data and verifying cumulative CRCs.</summary>
    /// <returns>The number of material data bytes consumed by the callbacks.</returns>
    public static long ReadData(Stream source, Action<SparseSequentialRegion, Stream> write,
        SparseHeader? expectedHeader = null, int maximumChunks = 262144, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write); var header = ReadHeader(source, maximumChunks, cancellationToken);
        if (expectedHeader is { } expected && expected != header) throw new SparseException(Strings.CompositionSourceInvalid);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            Skip(source, header.FileHeaderSize - 28, scratch, cancellationToken);
            long physical = header.FileHeaderSize, logical = 0, written = 0; var crc = new Crc32();
            Span<byte> chunk = stackalloc byte[12], value = stackalloc byte[4];
            for (uint index = 0; index < header.TotalChunks; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (physical > source.Length - header.ChunkHeaderSize) throw new SparseException(Strings.RawPayloadTruncated);
                ReadExactly(source, chunk, cancellationToken); Skip(source, header.ChunkHeaderSize - 12, scratch, cancellationToken);
                var type = (SparseChunkType)BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]), total = BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]);
                if (total < header.ChunkHeaderSize) throw new SparseException(Strings.CompositionSourceInvalid);
                long payload = total - header.ChunkHeaderSize, length = checked((long)blocks * header.BlockSize);
                if (physical > source.Length - total || length > header.RawLength - logical) throw new SparseException(Strings.CompositionSourceInvalid);
                uint fill = 0;
                switch (type)
                {
                    case SparseChunkType.Raw when payload == length: break;
                    case SparseChunkType.Fill when payload == 4:
                        ReadExactly(source, value, cancellationToken); fill = BinaryPrimitives.ReadUInt32LittleEndian(value); break;
                    case SparseChunkType.DontCare when payload == 0: break;
                    case SparseChunkType.Crc32 when payload == 4 && blocks == 0:
                        ReadExactly(source, value, cancellationToken);
                        if (BinaryPrimitives.ReadUInt32LittleEndian(value) != crc.GetCurrentHashAsUInt32()) throw new SparseException(Strings.SequentialChecksumInvalid);
                        break;
                    default: throw new SparseException(Strings.CompositionSourceInvalid);
                }
                if (type is SparseChunkType.Raw or SparseChunkType.Fill && length != 0)
                {
                    using var input = new RegionStream(type == SparseChunkType.Raw ? source : null, length, fill, crc, cancellationToken);
                    write(new(logical, length, type), input); cancellationToken.ThrowIfCancellationRequested();
                    if (input.Remaining != 0) throw new SparseException(Strings.SequentialRegionIncomplete);
                    written = checked(written + length);
                }
                else if (type == SparseChunkType.DontCare)
                {
                    scratch.AsSpan().Clear(); long remaining = length;
                    while (remaining > 0) { cancellationToken.ThrowIfCancellationRequested(); int n = (int)Math.Min(scratch.Length, remaining); crc.Append(scratch.AsSpan(0, n)); remaining -= n; }
                }
                physical = checked(physical + total); logical = checked(logical + length);
            }
            if (logical != header.RawLength || physical != source.Length) throw new SparseException(Strings.CompositionSourceInvalid);
            cancellationToken.ThrowIfCancellationRequested();
            if (source.ReadByte() != -1) throw new SparseException(Strings.CompositionSourceInvalid);
            if (header.ImageChecksum != 0 && header.ImageChecksum != crc.GetCurrentHashAsUInt32()) throw new SparseException(Strings.SequentialChecksumInvalid);
            return written;
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }
    private static void ReadExactly(Stream source, Span<byte> buffer, CancellationToken ct)
    {
        while (!buffer.IsEmpty) { ct.ThrowIfCancellationRequested(); int n = source.Read(buffer); if (n == 0) throw new SparseException(Strings.RawPayloadTruncated); buffer = buffer[n..]; }
        ct.ThrowIfCancellationRequested();
    }
    private static void Skip(Stream source, int count, byte[] scratch, CancellationToken ct)
    {
        while (count > 0) { int n = Math.Min(count, scratch.Length); ReadExactly(source, scratch.AsSpan(0, n), ct); count -= n; }
    }
    private sealed class RegionStream(Stream? source, long length, uint fill, Crc32 crc, CancellationToken ct) : Stream
    {
        private readonly long _length = length;
        private bool _disposed; internal long Remaining { get; private set; } = length;
        private void Check() { ObjectDisposedException.ThrowIf(_disposed, this); ct.ThrowIfCancellationRequested(); }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get { Check(); return _length; } }
        public override long Position { get { Check(); return _length - Remaining; } set => throw new NotSupportedException(Strings.StreamOperationNotSupported); }
        public override int Read(Span<byte> buffer)
        {
            Check(); int n = (int)Math.Min(Math.Min(buffer.Length, 65536), Remaining); if (n == 0) return 0;
            if (source is null) SparsePattern.Fill(buffer[..n], fill, _length - Remaining);
            else { n = source.Read(buffer[..n]); if (n == 0) throw new SparseException(Strings.RawPayloadTruncated); }
            Check(); crc.Append(buffer[..n]); Remaining -= n; return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(Strings.StreamOperationNotSupported);
        public override void SetLength(long value) => throw new NotSupportedException(Strings.StreamOperationNotSupported);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Strings.StreamOperationNotSupported);
        public override void Flush() => Check();
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    }
}

/// <summary>A data window in the logical Sparse image; its callback stream is forward-only.</summary>
public readonly record struct SparseSequentialRegion(long OutputOffset, long Length, SparseChunkType Type);
