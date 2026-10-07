using System.Buffers.Binary;
using System.Text;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using ZstdSharp;
using ZstdSharp.Unsafe;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal sealed record PayloadOperation(int Type, long Offset, long StoredLength, long OutputLength);
internal sealed record PayloadSegment(long Start, long Length, long OperationOffset, PayloadOperation Operation);
internal sealed record PayloadPartition(string Name, long Size, List<PayloadSegment> Segments);

internal static class PayloadParser
{
    internal static void Parse(ParseContext c)
    {
        byte[] header = c.Read(0, 24); if (!header.AsSpan(0, 4).SequenceEqual("CrAU"u8)) throw new InvalidDataException(Strings.InvalidMetadata);
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4)) != 2) throw new NotSupportedException(Strings.Unsupported);
        long manifestLength = ParseContext.Long(BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12))); c.Limit(manifestLength, c.Options.MaximumMetadataBytes);
        long data = checked(24 + manifestLength + BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20))); SourceStream.Range(c.Source.Length, data, 0);
        byte[] manifest = c.Read(24, checked((int)manifestLength)); var reader = new ProtoReader(manifest); long blockSize = 4096; int total = 0;
        var partitions = new List<byte[]>(); bool hasBlock = false;
        while (reader.Next(out var field))
        {
            c.Check();
            if (field.Number == 3) { field.Require(0); if (hasBlock) throw new InvalidDataException(Strings.InvalidMetadata); hasBlock = true; blockSize = ParseContext.Long(field.Scalar); }
            else if (field.Number == 13) { field.Require(2); c.Limit(partitions.Count + 1, c.Options.MaximumEntries); partitions.Add(field.Bytes.ToArray()); }
        }
        if (blockSize is < 512 or > 1048576 || (blockSize & (blockSize - 1)) != 0) throw new InvalidDataException(Strings.InvalidMetadata);
        foreach (byte[] bytes in partitions)
        {
            var p = Partition(c, bytes, blockSize, data, ref total); var segments = p.Segments.OrderBy(s => s.Start).ToArray(); long end = 0;
            foreach (var segment in segments)
            { if (segment.Start != end) throw new InvalidDataException(Strings.InvalidMetadata); end = checked(end + segment.Length); }
            if (end != p.Size) throw new InvalidDataException(Strings.InvalidMetadata);
            c.Add(p.Name + ".img", p.Size, ct => new PayloadStream(c, segments, p.Size, blockSize, ct));
        }
        if (partitions.Count == 0) throw new InvalidDataException(Strings.InvalidMetadata);
    }
    private static PayloadPartition Partition(ParseContext c, ReadOnlySpan<byte> bytes, long blockSize, long data, ref int total)
    {
        var reader = new ProtoReader(bytes); string? name = null; long? size = null; var raw = new List<byte[]>();
        while (reader.Next(out var f))
        {
            c.Check();
            if (f.Number == 1) { f.Require(2); if (name is not null) throw new InvalidDataException(Strings.InvalidMetadata); name = new UTF8Encoding(false, true).GetString(f.Bytes); }
            else if (f.Number == 7)
            {
                f.Require(2); if (size is not null) throw new InvalidDataException(Strings.InvalidMetadata); var info = new ProtoReader(f.Bytes);
                while (info.Next(out var a)) if (a.Number == 1) { a.Require(0); if (size is not null) throw new InvalidDataException(Strings.InvalidMetadata); size = ParseContext.Long(a.Scalar); }
            }
            else if (f.Number == 8) { f.Require(2); c.Limit(++total, c.Options.MaximumSegments); raw.Add(f.Bytes.ToArray()); }
        }
        if (name is null || size is null || size < 0) throw new InvalidDataException(Strings.InvalidMetadata);
        var result = new PayloadPartition(name, size.Value, []);
        foreach (byte[] op in raw) Operation(c, result, op, blockSize, data, ref total);
        return result;
    }
    private static void Operation(ParseContext c, PayloadPartition partition, ReadOnlySpan<byte> bytes, long blockSize, long data, ref int total)
    {
        var reader = new ProtoReader(bytes); int type = -1; long offset = 0, stored = 0; var extents = new List<(long Start, long Size)>();
        var seen = new HashSet<int>();
        while (reader.Next(out var f))
        {
            c.Check();
            if (f.Number is 1 or 2 or 3)
            {
                f.Require(0); if (!seen.Add(f.Number)) throw new InvalidDataException(Strings.InvalidMetadata);
                if (f.Number == 1) type = checked((int)f.Scalar); else if (f.Number == 2) offset = ParseContext.Long(f.Scalar); else stored = ParseContext.Long(f.Scalar);
            }
            else if (f.Number == 4) throw new NotSupportedException(Strings.UnsupportedOperation);
            else if (f.Number == 6)
            {
                f.Require(2); c.Limit(++total, c.Options.MaximumSegments); var r = new ProtoReader(f.Bytes); long? start = null, blocks = null;
                while (r.Next(out var e))
                {
                    if (e.Number == 1) { e.Require(0); if (start is not null) throw new InvalidDataException(Strings.InvalidMetadata); start = ParseContext.Long(e.Scalar); }
                    else if (e.Number == 2) { e.Require(0); if (blocks is not null) throw new InvalidDataException(Strings.InvalidMetadata); blocks = ParseContext.Long(e.Scalar); }
                }
                if (start is null || blocks is null || blocks <= 0) throw new InvalidDataException(Strings.InvalidMetadata);
                long byteStart = checked(start.Value * blockSize), length = checked(blocks.Value * blockSize);
                SourceStream.Range(partition.Size, byteStart, length); extents.Add((byteStart, length));
            }
        }
        if (type is not (0 or 1 or 6 or 7 or 8 or 14)) throw new NotSupportedException(Strings.UnsupportedOperation);
        if (extents.Count == 0) throw new InvalidDataException(Strings.InvalidMetadata);
        long output = 0; foreach (var e in extents) output = checked(output + e.Size);
        long startData = checked(data + offset); SourceStream.Range(c.Source.Length, startData, stored);
        if (type == 0 && (stored > output || output - stored >= blockSize) || type is 6 or 7 && stored != 0) throw new InvalidDataException(Strings.LengthMismatch);
        var operation = new PayloadOperation(type, startData, stored, output); long logical = 0;
        foreach (var e in extents) { partition.Segments.Add(new(e.Start, e.Size, logical, operation)); logical = checked(logical + e.Size); }
    }
    internal static Stream Decode(ParseContext c, PayloadOperation operation, CancellationToken ct)
    {
        Stream slice = new DecoderInputStream(SourceStream.Slice(c.Source, operation.Offset, operation.StoredLength, ct), ct);
        try
        {
            return operation.Type switch
            {
                0 => slice,
                1 => BZip2Stream.Create(slice, SharpCompress.Compressors.CompressionMode.Decompress, false),
                8 => Xz(c, slice, ct),
                14 => Zstd(c, slice),
                _ => throw new NotSupportedException(Strings.UnsupportedOperation)
            };
        }
        catch { slice.Dispose(); throw; }
    }
    private static Stream Xz(ParseContext c, Stream slice, CancellationToken ct)
    { CodecLimits.ValidateXz(slice, c.Options, ct); slice.Position = 0; return new OwnedStream(new XZStream(slice), slice); }
    private static Stream Zstd(ParseContext c, Stream slice)
    {
        var decoder = new DecompressionStream(slice, bufferSize: c.Options.BufferSize);
        try { decoder.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, System.Numerics.BitOperations.Log2((uint)c.Options.MaximumDecoderWindowBytes)); return decoder; }
        catch { decoder.Dispose(); throw; }
    }
}

internal sealed class PayloadStream(ParseContext context, PayloadSegment[] segments, long length, long blockSize, CancellationToken ct) : ReadOnlyStream(ct)
{
    private PayloadOperation? _operation;
    private Stream? _stream;
    public override long Length { get { Check(); return length; } }
    public override int Read(Span<byte> buffer)
    {
        Check(); if (buffer.Length == 0 || Cursor == length) return 0;
        int low = 0, high = segments.Length - 1;
        while (low < high) { int mid = low + (high - low + 1) / 2; if (segments[mid].Start <= Cursor) low = mid; else high = mid - 1; }
        var segment = segments[low]; long inside = Cursor - segment.Start; int count = (int)Math.Min(buffer.Length, segment.Length - inside);
        if (segment.Operation.Type is 6 or 7) { buffer[..count].Clear(); Cursor += count; return count; }
        if (!ReferenceEquals(_operation, segment.Operation))
        {
            _stream?.Dispose(); _stream = null; _operation = segment.Operation;
            _stream = new ReplayStream(() => PayloadParser.Decode(context, segment.Operation, Cancellation), segment.Operation.OutputLength, context.Options.BufferSize, Cancellation, blockSize - 1);
        }
        _stream!.Position = checked(segment.OperationOffset + inside); int n = _stream.Read(buffer[..count]); Cursor += n; return n;
    }
    protected override void Dispose(bool disposing) { if (disposing) _stream?.Dispose(); base.Dispose(disposing); }
}
