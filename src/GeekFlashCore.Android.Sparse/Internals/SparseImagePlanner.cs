using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using GeekFlashCore.Android.Sparse.Constants;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.Android.Sparse.Types;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;
using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.Android.Sparse.Internals;

internal static class SparseImagePlanner
{
    public static SparseImageWritePlan Analyze(
        IReadableBlockDevice source,
        SparseImageWriteOptions options,
        BudgetedArrayPool? buffers,
        CancellationToken cancellationToken = default)
    {
        long sourceLength = source.Length;
        if (sourceLength <= 0)
            throw new InvalidDataException(Strings.EmptyImage);

        ulong totalBlocks64 = checked(((ulong)sourceLength + (uint)options.BlockSize - 1) / (uint)options.BlockSize);
        if (totalBlocks64 > uint.MaxValue)
            throw new InvalidDataException(Strings.ValuesExceedSupportedLimits);

        uint totalBlocks = (uint)totalBlocks64;
        long expandedLength = checked((long)totalBlocks * options.BlockSize);
        var builder = new PlanBuilder(options, totalBlocks);
        uint checksum = 0;
        BudgetedArrayPool pool = buffers ?? new BudgetedArrayPool(new ByteBudget(BoundedStreamCopier.MaximumBufferSize));
        using PooledBufferLease lease = pool.Rent(options.BufferSize, cancellationToken);
        Span<byte> buffer = lease.Memory.Span;
        if (options.BlockSize <= buffer.Length)
        {
            AnalyzeBatches(
                source,
                options,
                totalBlocks,
                buffer,
                builder,
                ref checksum,
                cancellationToken);
        }
        else
        {
            long sourceOffset = 0;
            for (uint block = 0; block < totalBlocks; block++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BlockKind kind = ClassifyLargeBlock(
                    source,
                    sourceOffset,
                    options.BlockSize,
                    buffer,
                    options.IncludeCrc32Chunk,
                    ref checksum,
                    out uint fillValue,
                    cancellationToken);
                builder.Add(kind, block, fillValue);
                sourceOffset = Math.Min(sourceLength, checked(sourceOffset + options.BlockSize));
            }
        }

        return builder.Build(sourceLength, expandedLength, checksum);
    }

    private static void AnalyzeBatches(
        IReadableBlockDevice source,
        SparseImageWriteOptions options,
        uint totalBlocks,
        Span<byte> buffer,
        PlanBuilder builder,
        ref uint checksum,
        CancellationToken cancellationToken)
    {
        int blocksPerBatch = buffer.Length / options.BlockSize;
        uint block = 0;
        while (block < totalBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int blockCount = (int)Math.Min((uint)blocksPerBatch, totalBlocks - block);
            int logicalLength = checked(blockCount * options.BlockSize);
            long sourceOffset = checked((long)block * options.BlockSize);
            int sourceLength = (int)Math.Min(logicalLength, source.Length - sourceOffset);
            if (sourceLength != 0)
                BlockDeviceIO.ReadExactlyAt(source, sourceOffset, buffer[..sourceLength]);
            if (sourceLength != logicalLength)
                buffer[sourceLength..logicalLength].Clear();

            ReadOnlySpan<byte> batch = buffer[..logicalLength];
            if (options.IncludeCrc32Chunk)
                checksum = Crc32Helper.Append(checksum, batch);

            for (int index = 0; index < blockCount; index++)
            {
                ReadOnlySpan<byte> bytes = batch.Slice(index * options.BlockSize, options.BlockSize);
                BlockKind kind = ClassifyBufferedBlock(bytes, out uint fillValue);
                builder.Add(kind, checked(block + (uint)index), fillValue);
            }

            block = checked(block + (uint)blockCount);
        }
    }

    private static BlockKind ClassifyLargeBlock(
        IReadableBlockDevice source,
        long sourceOffset,
        int blockSize,
        Span<byte> buffer,
        bool computeChecksum,
        ref uint checksum,
        out uint fillValue,
        CancellationToken cancellationToken)
    {
        bool candidate = true;
        bool hasPattern = false;
        uint nativePattern = 0;
        fillValue = 0;
        long sourceCursor = sourceOffset;
        long sourceRemaining = Math.Max(0, source.Length - sourceOffset);
        long logicalRemaining = blockSize;

        while (logicalRemaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = (int)Math.Min(buffer.Length, logicalRemaining);
            int sourceLength = (int)Math.Min(length, sourceRemaining);
            if (sourceLength != 0)
                BlockDeviceIO.ReadExactlyAt(source, sourceCursor, buffer[..sourceLength]);
            if (sourceLength != length)
                buffer[sourceLength..length].Clear();

            ReadOnlySpan<byte> part = buffer[..length];
            if (candidate)
            {
                if (!hasPattern)
                {
                    nativePattern = MemoryMarshal.Read<uint>(part);
                    fillValue = BinaryPrimitives.ReadUInt32LittleEndian(part);
                    hasPattern = true;
                }

                candidate = IsRepeatedPattern(part, nativePattern);
            }

            if (computeChecksum)
                checksum = Crc32Helper.Append(checksum, part);

            logicalRemaining -= length;
            sourceCursor += sourceLength;
            sourceRemaining -= sourceLength;
        }

        if (candidate && fillValue == 0)
            return BlockKind.Zero;
        return candidate ? BlockKind.Fill : BlockKind.Raw;
    }

    private static BlockKind ClassifyBufferedBlock(
        ReadOnlySpan<byte> bytes,
        out uint fillValue)
    {
        uint nativePattern = MemoryMarshal.Read<uint>(bytes);
        fillValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (!IsRepeatedPattern(bytes, nativePattern))
            return BlockKind.Raw;
        return fillValue == 0 ? BlockKind.Zero : BlockKind.Fill;
    }

    private static bool IsRepeatedPattern(ReadOnlySpan<byte> bytes, uint nativePattern)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(bytes);
        int vectorLength = Vector<uint>.Count;
        if (Vector.IsHardwareAccelerated && words.Length >= vectorLength)
        {
            var expected = new Vector<uint>(nativePattern);
            int index = 0;
            for (; index <= words.Length - vectorLength; index += vectorLength)
            {
                if (!Vector.EqualsAll(new Vector<uint>(words[index..]), expected))
                    return false;
            }

            for (; index < words.Length; index++)
            {
                if (words[index] != nativePattern)
                    return false;
            }

            return true;
        }

        foreach (uint word in words)
        {
            if (word != nativePattern)
                return false;
        }

        return true;
    }

    private enum BlockKind
    {
        Raw,
        Fill,
        Zero
    }

    private sealed class PlanBuilder(SparseImageWriteOptions options, uint totalBlocks)
    {
        private readonly List<SparseImageWriteChunk> _chunks = [];
        private readonly int _maxRawBlocks = options.MaxRawChunkBlocks;

        public void Add(BlockKind kind, uint block, uint fillValue)
        {
            SparseChunkType type = kind switch
            {
                BlockKind.Raw => SparseChunkType.Raw,
                BlockKind.Fill => options.DetectFillChunks ? SparseChunkType.Fill : SparseChunkType.Raw,
                BlockKind.Zero => options.UseDontCareForZeroBlocks ? SparseChunkType.DontCare :
                    options.DetectFillChunks ? SparseChunkType.Fill : SparseChunkType.Raw,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            uint value = type == SparseChunkType.Fill ? fillValue : 0;

            if (_chunks.Count != 0)
            {
                SparseImageWriteChunk previous = _chunks[^1];
                bool contiguous = checked(previous.StartBlock + previous.BlockCount) == block;
                bool sameValue = previous.Type != SparseChunkType.Fill || previous.FillValue == value;
                bool canMergeRaw = previous.Type != SparseChunkType.Raw || previous.BlockCount < _maxRawBlocks;
                if (contiguous && previous.Type == type && sameValue && canMergeRaw)
                {
                    uint mergedCount = checked(previous.BlockCount + 1);
                    if (type != SparseChunkType.Raw || mergedCount <= _maxRawBlocks)
                    {
                        _chunks[^1] = new SparseImageWriteChunk(type, previous.StartBlock, mergedCount, value);
                        return;
                    }
                }
            }

            EnsureChunkCapacity();
            _chunks.Add(new SparseImageWriteChunk(type, block, 1, value));
        }

        public SparseImageWritePlan Build(long sourceLength, long expandedLength, uint checksum)
        {
            bool includeCrc = options.IncludeCrc32Chunk;
            long encodedLength = SparseConstant.HeaderLength;
            foreach (SparseImageWriteChunk chunk in _chunks)
            {
                long payload = chunk.Type switch
                {
                    SparseChunkType.Raw => checked((long)chunk.BlockCount * options.BlockSize),
                    SparseChunkType.Fill => sizeof(uint),
                    SparseChunkType.DontCare => 0,
                    _ => throw new InvalidDataException(Strings.UnsupportedChunkType)
                };
                encodedLength = checked(encodedLength + SparseConstant.ChunkLength + payload);
            }

            if (includeCrc)
                encodedLength = checked(encodedLength + SparseConstant.ChunkLength + sizeof(uint));

            return new SparseImageWritePlan(
                sourceLength,
                expandedLength,
                checked((uint)options.BlockSize),
                totalBlocks,
                [.. _chunks],
                encodedLength,
                includeCrc,
                checksum);
        }

        private void EnsureChunkCapacity()
        {
            int maximum = options.MaxChunkCount - (options.IncludeCrc32Chunk ? 1 : 0);
            if (_chunks.Count >= maximum)
                throw new InvalidDataException(Strings.ChunkLimitExceeded);
        }
    }
}
