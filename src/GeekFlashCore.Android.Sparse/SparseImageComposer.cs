using System.Buffers.Binary;
using GeekFlashCore.Android.Sparse.Types;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Sparse;

/// <summary>Builds a seekable sparse encoding over ordered fragments of the same logical image.</summary>
public static partial class SparseImageComposer
{
    /// <summary>Validates sources and maps later RAW/FILL data over earlier data without expanding RAW.</summary>
    /// <remarks>Each factory transfers ownership of its opened stream to this API, at any position. Factories remain borrowed.
    /// Source checksums, when present, are verified before publishing the result; output checksum is absent.</remarks>
    public static SparseImageComposition Compose(IReadOnlyList<Func<CancellationToken, Stream>> sources,
        SparseImageCompositionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources); options ??= new(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        int sourceCount = sources.Count;
        if (sourceCount < 1 || sourceCount > options.MaximumSources) throw new SparseException(Strings.CompositionLimitExceeded);
        var factories = new Func<CancellationToken, Stream>[sourceCount];
        for (int i = 0; i < sourceCount; i++) { cancellationToken.ThrowIfCancellationRequested(); factories[i] = sources[i]; ArgumentNullException.ThrowIfNull(factories[i]); }
        var snapshots = new CompositionSource[factories.Length];
        var ranges = new List<DataRange>(); uint blockSize = 0, totalBlocks = 0; long totalChunks = 0;
        Span<byte> header = stackalloc byte[28];
        for (int index = 0; index < factories.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(factories[index]);
            using Stream input = factories[index](cancellationToken); ValidateSource(input); input.Position = 0;
            input.ReadExactly(header);
            totalChunks = checked(totalChunks + BinaryPrimitives.ReadUInt32LittleEndian(header[20..]));
            if (totalChunks > options.MaximumChunks || totalChunks * 512 > options.MaximumMetadataBytes)
                throw new SparseException(Strings.CompositionLimitExceeded);
            input.Position = 0;
            using var device = new StreamBlockDevice(input, input.Length, DeviceOwnership.Borrow, 1);
            using var document = SparseImageParser.Open(new CancelledDevice(device, cancellationToken), DeviceOwnership.Borrow);
            if (index == 0) { blockSize = document.Header.BlockSize; totalBlocks = document.Header.TotalBlocks; }
            else if (blockSize != document.Header.BlockSize || totalBlocks != document.Header.TotalBlocks)
                throw new SparseException(Strings.CompositionGeometryMismatch);
            if (document.ParsedPhysicalLength != input.Length) throw new SparseException(Strings.CompositionSourceInvalid);
            if (document.ChecksumStatus == SparseChecksumStatus.NotVerified)
                document.VerifyChecksum(cancellationToken: cancellationToken);
            snapshots[index] = new(factories[index], input.Length);
            foreach (var chunk in document.Chunks.Span)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chunk.Type is SparseChunkType.Raw or SparseChunkType.Fill && chunk.OutputLength > 0)
                    ranges.Add(new(chunk.OutputOffset, checked(chunk.OutputOffset + chunk.OutputLength), index,
                        chunk.PayloadOffset, chunk.Type, chunk.FillValue));
            }
        }
        return Build(snapshots, ranges, blockSize, totalBlocks, options, cancellationToken);
    }

    internal static void ValidateSource(Stream? source, int minimumLength = 28)
    {
        if (source is null || !source.CanRead || !source.CanSeek || source.Length < minimumLength)
            throw new SparseException(Strings.CompositionSourceInvalid);
    }

    private static SparseImageComposition Build(CompositionSource[] sources, List<DataRange> ranges,
        uint blockSize, uint totalBlocks, SparseImageCompositionOptions options, CancellationToken ct)
    {
        var events = new Boundary[checked(ranges.Count * 2)];
        for (int i = 0; i < ranges.Count; i++) { ct.ThrowIfCancellationRequested(); events[i * 2] = new(ranges[i].Start, i, true); events[i * 2 + 1] = new(ranges[i].End, i, false); }
        Array.Sort(events, static (a, b) => a.Offset.CompareTo(b.Offset)); ct.ThrowIfCancellationRequested();
        var active = new SortedSet<int>(); var chunks = new List<CompositionChunk>(); long cursor = 0;
        int boundary = 0;
        while (boundary < events.Length)
        {
            ct.ThrowIfCancellationRequested(); long next = events[boundary].Offset;
            Emit(cursor, next); cursor = next;
            do { var e = events[boundary++]; if (e.Starts) active.Add(e.Range); else active.Remove(e.Range); }
            while (boundary < events.Length && events[boundary].Offset == next);
        }
        Emit(cursor, checked((long)blockSize * totalBlocks));
        long encoded = 28;
        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var chunk = chunks[i]; chunks[i] = chunk with { EncodedOffset = encoded };
            long payload = chunk.Type == SparseChunkType.Raw ? checked((long)chunk.BlockCount * blockSize) : chunk.Type == SparseChunkType.Fill ? 4 : 0;
            encoded = checked(encoded + 12 + payload);
        }
        return new(sources, chunks.ToArray(), blockSize, totalBlocks, encoded, options.MaximumOpenSources, ct);

        void Emit(long start, long end)
        {
            if (start == end) return;
            DataRange? range = active.Count == 0 ? null : ranges[active.Max];
            SparseChunkType type = range?.Type ?? SparseChunkType.DontCare;
            uint remaining = checked((uint)((end - start) / blockSize)), block = checked((uint)(start / blockSize));
            long payload = range is { Type: SparseChunkType.Raw } r ? checked(r.Payload + start - r.Start) : 0;
            int source = range?.Source ?? -1; uint fill = range?.Fill ?? 0;
            uint maximum = type == SparseChunkType.Raw ? (uint)((uint.MaxValue - 12L) / blockSize) : uint.MaxValue;
            while (remaining != 0)
            {
                ct.ThrowIfCancellationRequested(); uint count = Math.Min(remaining, maximum);
                var chunk = new CompositionChunk(0, block, count, type, source, type == SparseChunkType.Raw ? payload : 0, fill);
                if (chunks.Count > 0 && CanMerge(chunks[^1], chunk, blockSize, maximum))
                    chunks[^1] = chunks[^1] with { BlockCount = checked(chunks[^1].BlockCount + count) };
                else { if (chunks.Count >= options.MaximumChunks) throw new SparseException(Strings.CompositionLimitExceeded); chunks.Add(chunk); }
                remaining -= count; block = checked(block + count);
                if (type == SparseChunkType.Raw) payload = checked(payload + (long)count * blockSize);
            }
        }
    }
    private static bool CanMerge(CompositionChunk a, CompositionChunk b, uint blockSize, uint maximum) =>
        a.Type == b.Type && (ulong)a.StartBlock + a.BlockCount == b.StartBlock && (ulong)a.BlockCount + b.BlockCount <= maximum &&
        (a.Type == SparseChunkType.DontCare || a.Type == SparseChunkType.Fill && a.FillValue == b.FillValue ||
         a.Type == SparseChunkType.Raw && a.SourceIndex == b.SourceIndex && checked(a.PayloadOffset + (long)a.BlockCount * blockSize) == b.PayloadOffset);

    private readonly record struct DataRange(long Start, long End, int Source, long Payload, SparseChunkType Type, uint Fill);
    private readonly record struct Boundary(long Offset, int Range, bool Starts);
    private sealed class CancelledDevice(IReadableBlockDevice inner, CancellationToken ct) : IReadableBlockDevice
    {
        public BlockDeviceId Id => inner.Id;
        public long Length => inner.Length;
        public int LogicalBlockSize => inner.LogicalBlockSize;
        public int ReadAt(long offset, Span<byte> destination) { ct.ThrowIfCancellationRequested(); int n = inner.ReadAt(offset, destination); ct.ThrowIfCancellationRequested(); return n; }
        public void Dispose() { }
    }
}
