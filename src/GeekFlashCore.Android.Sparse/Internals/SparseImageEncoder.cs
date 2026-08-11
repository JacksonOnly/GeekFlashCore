using System.Buffers.Binary;
using GeekFlashCore.Android.Sparse.Constants;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.Android.Sparse.Types;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Sparse.Internals;


internal static class SparseImageEncoder
{
    public static void Write(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWritePlan plan,
        int bufferSize,
        IProgress<BlockCopyProgress>? progress,
        BudgetedArrayPool? buffers)
    {
        Span<byte> header = stackalloc byte[SparseConstant.HeaderLength];
        WriteFileHeader(header, plan);
        destination.Write(header);

        PooledBufferLease? lease = null;
        try
        {
            SparseImageWriteChunk[] chunks = plan.ChunkArray;
            if (HasRawChunks(chunks))
            {
                BudgetedArrayPool pool = buffers ??
                    new BudgetedArrayPool(new ByteBudget(BoundedStreamCopier.MaximumBufferSize));
                lease = pool.Rent(bufferSize);
            }

            Span<byte> buffer = lease is null ? Span<byte>.Empty : lease.Memory.Span;
            long completed = 0;
            foreach (SparseImageWriteChunk chunk in chunks)
            {
                WriteChunkHeader(destination, chunk, plan.BlockSize);
                switch (chunk.Type)
                {
                    case SparseChunkType.Raw:
                        WriteRaw(source, destination, chunk, plan.BlockSize, buffer);
                        break;
                    case SparseChunkType.Fill:
                        WriteFill(destination, chunk.FillValue);
                        break;
                    case SparseChunkType.DontCare:
                        break;
                    default:
                        throw new SparseException(Strings.UnsupportedChunkType);
                }

                completed = checked(completed + (long)chunk.BlockCount * plan.BlockSize);
                progress?.Report(new BlockCopyProgress(completed, plan.RawLength));
            }
        }
        finally
        {
            lease?.Dispose();
        }

        if (plan.IncludesCrc32Chunk)
        {
            Span<byte> crcHeader = stackalloc byte[SparseConstant.ChunkLength];
            WriteChunkHeader(
                crcHeader,
                SparseChunkType.Crc32,
                0,
                SparseConstant.ChunkLength + sizeof(uint));
            destination.Write(crcHeader);
            Span<byte> crc = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(crc, plan.Checksum!.Value);
            destination.Write(crc);
        }
    }

    public static async ValueTask WriteAsync(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWritePlan plan,
        int bufferSize,
        IProgress<BlockCopyProgress>? progress,
        BudgetedArrayPool? buffers,
        CancellationToken cancellationToken)
    {
        BudgetedArrayPool pool = buffers ?? new BudgetedArrayPool(new ByteBudget(BoundedStreamCopier.MaximumBufferSize));
        SparseImageWriteChunk[] chunks = plan.ChunkArray;
        int rentalLength = HasRawChunks(chunks)
            ? Math.Max(bufferSize, SparseConstant.HeaderLength)
            : SparseConstant.HeaderLength;
        await using PooledBufferLease lease = await pool
            .RentAsync(rentalLength, cancellationToken)
            .ConfigureAwait(false);
        Memory<byte> rentedMemory = lease.Memory;
        Memory<byte> buffer = rentedMemory[..Math.Min(bufferSize, rentedMemory.Length)];
        Memory<byte> metadata = rentedMemory[..SparseConstant.HeaderLength];

        WriteFileHeader(metadata.Span, plan);
        await destination
            .WriteAsync(metadata, cancellationToken)
            .ConfigureAwait(false);

        long completed = 0;
        for (int index = 0; index < chunks.Length; index++)
        {
            SparseImageWriteChunk chunk = chunks[index];
            cancellationToken.ThrowIfCancellationRequested();
            WriteChunkHeader(metadata.Span, chunk, plan.BlockSize);
            await destination
                .WriteAsync(metadata[..SparseConstant.ChunkLength], cancellationToken)
                .ConfigureAwait(false);
            switch (chunk.Type)
            {
                case SparseChunkType.Raw:
                    await WriteRawAsync(source, destination, chunk, plan.BlockSize, buffer, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case SparseChunkType.Fill:
                    BinaryPrimitives.WriteUInt32LittleEndian(metadata.Span, chunk.FillValue);
                    await destination.WriteAsync(metadata[..sizeof(uint)], cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case SparseChunkType.DontCare:
                    break;
                default:
                    throw new SparseException(Strings.UnsupportedChunkType);
            }

            completed = checked(completed + (long)chunk.BlockCount * plan.BlockSize);
            progress?.Report(new BlockCopyProgress(completed, plan.RawLength));
        }

        if (plan.IncludesCrc32Chunk)
        {
            WriteChunkHeader(
                metadata.Span,
                SparseChunkType.Crc32,
                0,
                SparseConstant.ChunkLength + sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(
                metadata.Span[SparseConstant.ChunkLength..],
                plan.Checksum!.Value);
            await destination
                .WriteAsync(metadata[..(SparseConstant.ChunkLength + sizeof(uint))], cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool HasRawChunks(SparseImageWriteChunk[] chunks)
    {
        foreach (SparseImageWriteChunk chunk in chunks)
        {
            if (chunk.Type == SparseChunkType.Raw)
                return true;
        }

        return false;
    }

    private static void WriteRaw(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWriteChunk chunk,
        uint blockSize,
        Span<byte> buffer)
    {
        long sourceOffset = checked((long)chunk.StartBlock * blockSize);
        long remaining = checked((long)chunk.BlockCount * blockSize);
        while (remaining != 0)
        {
            int length = (int)Math.Min(buffer.Length, remaining);
            ReadLogicalBytes(source, sourceOffset, buffer[..length]);
            destination.Write(buffer[..length]);
            sourceOffset = checked(sourceOffset + length);
            remaining -= length;
        }
    }

    private static async ValueTask WriteRawAsync(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWriteChunk chunk,
        uint blockSize,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        long sourceOffset = checked((long)chunk.StartBlock * blockSize);
        long remaining = checked((long)chunk.BlockCount * blockSize);
        while (remaining != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = (int)Math.Min(buffer.Length, remaining);
            ReadLogicalBytes(source, sourceOffset, buffer.Span[..length]);
            await destination.WriteAsync(buffer[..length], cancellationToken).ConfigureAwait(false);
            sourceOffset = checked(sourceOffset + length);
            remaining -= length;
        }
    }

    private static void ReadLogicalBytes(
        IReadableBlockDevice source,
        long sourceOffset,
        Span<byte> destination)
    {
        long available = sourceOffset < source.Length ? source.Length - sourceOffset : 0;
        int sourceLength = (int)Math.Min(destination.Length, Math.Max(0, available));
        if (sourceLength != 0)
            BlockDeviceIO.ReadExactlyAt(source, sourceOffset, destination[..sourceLength]);
        if (sourceLength != destination.Length)
            destination[sourceLength..].Clear();
    }

    private static void WriteFill(Stream destination, uint fillValue)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, fillValue);
        destination.Write(value);
    }

    private static void WriteFileHeader(Span<byte> destination, SparseImageWritePlan plan)
    {
        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, SparseConstant.HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], SparseConstant.HeaderMajorVer);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], SparseConstant.HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], SparseConstant.ChunkLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], plan.BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], plan.TotalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], plan.ChunkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], 0);
    }

    private static void WriteChunkHeader(
        Stream destination,
        SparseImageWriteChunk chunk,
        uint blockSize)
    {
        Span<byte> header = stackalloc byte[SparseConstant.ChunkLength];
        WriteChunkHeader(header, chunk, blockSize);
        destination.Write(header);
    }

    private static void WriteChunkHeader(
        Span<byte> destination,
        SparseImageWriteChunk chunk,
        uint blockSize)
    {
        uint payloadLength = chunk.Type switch
        {
            SparseChunkType.Raw => checked((uint)((long)chunk.BlockCount * blockSize)),
            SparseChunkType.Fill => sizeof(uint),
            SparseChunkType.DontCare => 0,
            _ => throw new SparseException(Strings.UnsupportedChunkType)
        };
        WriteChunkHeader(
            destination,
            chunk.Type,
            chunk.BlockCount,
            checked((uint)(SparseConstant.ChunkLength + payloadLength)));
    }

    private static void WriteChunkHeader(
        Span<byte> destination,
        SparseChunkType type,
        uint blockCount,
        uint totalSize)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], blockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], totalSize);
    }
}
