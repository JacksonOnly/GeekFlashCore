using System.Buffers.Binary;
using GeekFlashCore.Android.Sparse.Types;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Sparse;

public static partial class SparseImageComposer
{
    /// <summary>Maps ordered, disjoint logical windows from RAW/Sparse sources to a virtual Sparse encoding.</summary>
    /// <remarks>Unmapped ranges and source DONT_CARE remain DONT_CARE. Factories are borrowed;
    /// parsing streams are disposed here and each output stream owns its reopened source streams.</remarks>
    public static SparseImageComposition ComposeLayout(IReadOnlyList<SparseImageInput> sources,
        IReadOnlyList<SparseImagePlacement> placements, uint blockSize, uint totalBlocks,
        SparseImageCompositionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(placements);
        options ??= new(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        int sourceCount = sources.Count, placementCount = placements.Count;
        if (blockSize < 4 || blockSize > uint.MaxValue - 12 || blockSize % 4 != 0 || totalBlocks == 0 || (ulong)blockSize * totalBlocks > long.MaxValue)
            throw new SparseException(Strings.CompositionGeometryMismatch);
        if (sourceCount < 1 || sourceCount > options.MaximumSources || placementCount > options.MaximumChunks ||
            placementCount * 128L + sourceCount * 128L > options.MaximumMetadataBytes)
            throw new SparseException(Strings.CompositionLimitExceeded);
        long outputLength = checked((long)blockSize * totalBlocks), previousEnd = 0;
        var windows = new SparseImagePlacement[placementCount];
        for (int i = 0; i < placementCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var p = placements[i];
            if ((uint)p.SourceIndex >= (uint)sourceCount || p.SourceOffset < 0 || p.OutputOffset < previousEnd || p.Length <= 0 ||
                p.SourceOffset % blockSize != 0 || p.OutputOffset % blockSize != 0 || p.Length % blockSize != 0 ||
                p.OutputOffset > outputLength || p.Length > outputLength - p.OutputOffset)
                throw new SparseException(Strings.CompositionPlacementInvalid);
            previousEnd = p.OutputOffset + p.Length; windows[i] = p;
        }
        var inputs = new SparseImageInput[sourceCount];
        for (int i = 0; i < sourceCount; i++) { inputs[i] = sources[i]; ArgumentNullException.ThrowIfNull(inputs[i]); ArgumentNullException.ThrowIfNull(inputs[i].OpenStream); }
        var snapshots = new CompositionSource[sourceCount]; var logicalLengths = new long[sourceCount];
        var inputRanges = new List<DataRange>[sourceCount]; long inputChunks = 0; Span<byte> header = stackalloc byte[28];
        for (int i = 0; i < sourceCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var input = inputs[i]; using Stream stream = input.OpenStream(cancellationToken);
            ValidateSource(stream, input.IsSparse ? 28 : 0); snapshots[i] = new(input.OpenStream, stream.Length);
            var ranges = inputRanges[i] = [];
            if (input.IsSparse)
            {
                stream.Position = 0; stream.ReadExactly(header); inputChunks = checked(inputChunks + BinaryPrimitives.ReadUInt32LittleEndian(header[20..]));
                Budget(); stream.Position = 0;
                using var device = new StreamBlockDevice(stream, stream.Length, DeviceOwnership.Borrow, 1);
                using var document = SparseImageParser.Open(new CancelledDevice(device, cancellationToken), DeviceOwnership.Borrow);
                if (document.Header.BlockSize != blockSize) throw new SparseException(Strings.CompositionGeometryMismatch);
                if (document.ParsedPhysicalLength != stream.Length) throw new SparseException(Strings.CompositionSourceInvalid);
                if (document.ChecksumStatus == SparseChecksumStatus.NotVerified) document.VerifyChecksum(cancellationToken: cancellationToken);
                logicalLengths[i] = document.ExpandedLength;
                foreach (var chunk in document.Chunks.Span)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (chunk.Type is SparseChunkType.Raw or SparseChunkType.Fill && chunk.OutputLength != 0)
                        ranges.Add(new(chunk.OutputOffset, checked(chunk.OutputOffset + chunk.OutputLength), i, chunk.PayloadOffset, chunk.Type, chunk.FillValue));
                }
            }
            else
            {
                inputChunks++; Budget(); logicalLengths[i] = stream.Length;
                if (stream.Length != 0) ranges.Add(new(0, stream.Length, i, 0, SparseChunkType.Raw, 0));
            }
            if (input.ExpectedLogicalLength is { } expected && expected != logicalLengths[i])
                throw new SparseException(Strings.CompositionSourceInvalid);
        }
        var mapped = new List<DataRange>();
        foreach (var p in windows)
        {
            cancellationToken.ThrowIfCancellationRequested(); long length = logicalLengths[p.SourceIndex];
            if (p.SourceOffset > length || p.Length > length - p.SourceOffset) throw new SparseException(Strings.CompositionPlacementInvalid);
            var ranges = inputRanges[p.SourceIndex]; int low = 0, high = ranges.Count;
            while (low < high) { int middle = low + (high - low) / 2; if (ranges[middle].End <= p.SourceOffset) low = middle + 1; else high = middle; }
            long end = checked(p.SourceOffset + p.Length);
            for (int i = low; i < ranges.Count && ranges[i].Start < end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var r = ranges[i]; long start = Math.Max(r.Start, p.SourceOffset), stop = Math.Min(r.End, end);
                if (mapped.Count >= options.MaximumChunks || (inputChunks + mapped.Count + 1) * 512 + (placementCount + sourceCount) * 128L > options.MaximumMetadataBytes)
                    throw new SparseException(Strings.CompositionLimitExceeded);
                mapped.Add(new(checked(p.OutputOffset + start - p.SourceOffset), checked(p.OutputOffset + stop - p.SourceOffset),
                    p.SourceIndex, r.Type == SparseChunkType.Raw ? checked(r.Payload + start - r.Start) : 0, r.Type, r.Fill));
            }
        }
        return Build(snapshots, mapped, blockSize, totalBlocks, options, cancellationToken);

        void Budget()
        {
            if (inputChunks > options.MaximumChunks || inputChunks * 512 + (placementCount + sourceCount) * 128L > options.MaximumMetadataBytes)
                throw new SparseException(Strings.CompositionLimitExceeded);
        }
    }
}
