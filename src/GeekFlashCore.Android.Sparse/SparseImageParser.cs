using System.Buffers.Binary;
using System.Globalization;
using GeekFlashCore.Android.Sparse.Constants;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.Android.Sparse.Types;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Sparse;


public static class SparseImageParser
{
    public static bool IsSparse(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            return false;

        long position = source.Position;
        try
        {
            Span<byte> magic = stackalloc byte[sizeof(uint)];
            return TryReadExactly(source, magic)
                && BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseConstant.HeaderMagic;
        }
        finally
        {
            source.Position = position;
        }
    }
    public static SparseDocument Open(
        IReadableBlockDevice source,
        DeviceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(ownership)) throw new ArgumentOutOfRangeException(nameof(ownership));

        try
        {
            return OpenCore(source, ownership);
        }
        catch (Exception exception) when (exception is BlockDeviceException or IOException)
        {
            throw SparseFailure(
                Strings.SourceCouldNotBeRead,
                source,
                0,
                exception);
        }
        catch (OverflowException exception)
        {
            throw SparseFailure(
                Strings.MetadataOverflow,
                source,
                0,
                exception);
        }
    }

    private static SparseDocument OpenCore(
        IReadableBlockDevice source,
        DeviceOwnership ownership)
    {
        Span<byte> bytes = stackalloc byte[SparseConstant.HeaderLength];
        BlockDeviceIO.ReadExactlyAt(source, 0, bytes);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        ushort minorVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        ushort fileHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        ushort chunkHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        uint totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        uint imageChecksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);

        if (magic != SparseConstant.HeaderMagic)
        {
            throw SparseFailure(
                Strings.InvalidMagic,
                source,
                0);
        }

        if (majorVersion != SparseConstant.HeaderMajorVer)
        {
            throw SparseFailure(
                Strings.FormatUnsupportedVersion(majorVersion),
                source,
                4,
                featureId: majorVersion);
        }

        if (fileHeaderSize < SparseConstant.HeaderLength || chunkHeaderSize < SparseConstant.ChunkLength)
        {
            throw Corrupt(source, 8, Strings.InvalidHeaderSize);
        }

        if (blockSize == 0 || (blockSize & 3) != 0 || blockSize > int.MaxValue)
        {
            throw Corrupt(source, 12, Strings.InvalidBlockSize);
        }

        if (totalBlocks == 0 || totalChunks == 0)
        {
            throw Corrupt(source, 16, Strings.EmptyImage);
        }

        if (totalChunks > Array.MaxLength)
        {
            throw SparseFailure(
                Strings.ChunkLimitExceeded,
                source,
                20);
        }

        if (fileHeaderSize > source.Length)
        {
            throw Truncated(source, fileHeaderSize, Strings.FileHeaderExceedsSource);
        }

        long availableForHeaders = source.Length - fileHeaderSize;
        if (totalChunks > (ulong)(availableForHeaders / chunkHeaderSize))
        {
            throw Truncated(source, fileHeaderSize, Strings.ChunkHeadersExceedSource);
        }

        var header = new SparseHeader(
            majorVersion,
            minorVersion,
            fileHeaderSize,
            chunkHeaderSize,
            blockSize,
            totalBlocks,
            totalChunks,
            imageChecksum);
        long rawLength = header.RawLength;
        var chunks = GC.AllocateUninitializedArray<SparseChunk>((int)totalChunks);
        long physicalOffset = fileHeaderSize;
        long outputOffset = 0;
        Span<byte> chunkHeader = stackalloc byte[SparseConstant.ChunkLength];
        Span<byte> valueBytes = stackalloc byte[sizeof(uint)];

        for (int index = 0; index < chunks.Length; index++)
        {
            BlockDeviceIO.ReadExactlyAt(source, physicalOffset, chunkHeader);
            ushort typeValue = BinaryPrimitives.ReadUInt16LittleEndian(chunkHeader);
            uint chunkBlocks = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[8..]);
            if (totalSize < chunkHeaderSize)
            {
                throw CorruptChunk(source, physicalOffset, index, Strings.ChunkTotalSizeTooSmall);
            }

            long payloadOffset = checked(physicalOffset + chunkHeaderSize);
            uint payloadLength = totalSize - chunkHeaderSize;
            if (payloadOffset > source.Length - payloadLength)
            {
                throw Truncated(
                    source,
                    payloadOffset,
                    Strings.FormatChunkPayloadExceedsSource(index),
                    index);
            }

            long outputLength = checked((long)chunkBlocks * blockSize);
            SparseChunkType type = (SparseChunkType)typeValue;
            uint value = 0;

            switch (type)
            {
                case SparseChunkType.Raw:
                    if (outputLength > uint.MaxValue || payloadLength != (uint)outputLength)
                    {
                        throw CorruptChunk(source, physicalOffset, index, Strings.RawPayloadSizeMismatch);
                    }
                    break;

                case SparseChunkType.Fill:
                    if (payloadLength != sizeof(uint))
                    {
                        throw CorruptChunk(source, physicalOffset, index, Strings.FillPayloadSizeInvalid);
                    }
                    BlockDeviceIO.ReadExactlyAt(source, payloadOffset, valueBytes);
                    value = BinaryPrimitives.ReadUInt32LittleEndian(valueBytes);
                    break;

                case SparseChunkType.DontCare:
                    if (payloadLength != 0)
                    {
                        throw CorruptChunk(source, physicalOffset, index, Strings.DontCarePayloadNotEmpty);
                    }
                    break;

                case SparseChunkType.Crc32:
                    if (chunkBlocks != 0 || payloadLength != sizeof(uint))
                    {
                        throw CorruptChunk(source, physicalOffset, index, Strings.Crc32PayloadInvalid);
                    }
                    BlockDeviceIO.ReadExactlyAt(source, payloadOffset, valueBytes);
                    value = BinaryPrimitives.ReadUInt32LittleEndian(valueBytes);
                    break;

                default:
                    throw SparseFailure(
                        Strings.FormatUnsupportedChunkType(
                            typeValue.ToString("X4", CultureInfo.InvariantCulture)),
                        source,
                        physicalOffset,
                        featureId: typeValue,
                        objectId: $"chunk:{index}");
            }

            chunks[index] = new SparseChunk(outputOffset, outputLength, payloadOffset, type, value);
            outputOffset = checked(outputOffset + outputLength);
            physicalOffset = checked(payloadOffset + payloadLength);
        }

        if (outputOffset != rawLength)
        {
            throw Corrupt(
                source,
                physicalOffset,
                Strings.FormatExpandedLengthMismatch(outputOffset, rawLength));
        }

        return new SparseDocument(source, header, chunks, physicalOffset, ownership);
    }
    private static SparseException Corrupt(
        IReadableBlockDevice source,
        long offset,
        string message) =>
        SparseFailure(message, source, offset);

    private static SparseException CorruptChunk(
        IReadableBlockDevice source,
        long offset,
        int index,
        string reason) =>
        SparseFailure(
            Strings.FormatInvalidChunk(index, reason),
            source,
            offset,
            objectId: $"chunk:{index}");

    private static SparseException Truncated(
        IReadableBlockDevice source,
        long offset,
        string message,
        int? index = null) =>
        SparseFailure(
            message,
            source,
            offset,
            objectId: index is null ? null : $"chunk:{index.Value}");

    private static SparseException SparseFailure(
        string message,
        IReadableBlockDevice source,
        long offset,
        Exception? innerException = null,
        ulong? featureId = null,
        string? objectId = null)
    {
        string context = $"blockDeviceId: {source.Id}; deviceRelativeOffset: {offset}";
        if (objectId is not null)
            context += $"; objectId: {objectId}";
        if (featureId is not null)
            context += $"; featureId: {featureId.Value}";

        return new SparseException($"{message} ({context})", innerException);
    }

    public static SparseImage Parse(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException(Strings.SourceMustBeReadableAndSeekable, nameof(source));

        long origin = source.Position;
        try
        {
            return ParseCore(source);
        }
        catch (EndOfStreamException exception)
        {
            throw new SparseException(Strings.ImageTruncated, exception);
        }
        catch (OverflowException exception)
        {
            throw new SparseException(Strings.ValuesExceedSupportedLimits, exception);
        }
        finally
        {
            source.Position = origin;
        }
    }

    private static SparseImage ParseCore(Stream source)
    {
        Span<byte> bytes = stackalloc byte[SparseConstant.HeaderLength];
        source.ReadExactly(bytes);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        ushort minorVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        ushort fileHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        ushort chunkHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        uint totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        uint imageChecksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);

        if (magic != SparseConstant.HeaderMagic)
            throw new SparseException(Strings.StreamInvalidMagic);
        if (majorVersion != SparseConstant.HeaderMajorVer)
            throw new SparseException(Strings.FormatUnsupportedLegacyVersion(majorVersion));
        if (fileHeaderSize < SparseConstant.HeaderLength || chunkHeaderSize < SparseConstant.ChunkLength)
            throw new SparseException(Strings.LegacyInvalidHeaderSize);
        if (blockSize == 0 || (blockSize & 3) != 0)
            throw new SparseException(Strings.LegacyInvalidBlockSize);

        Skip(source, fileHeaderSize - SparseConstant.HeaderLength);
        if (totalChunks > (ulong)(source.Length - source.Position) / chunkHeaderSize)
            throw new SparseException(Strings.LegacyChunkHeadersMissing);

        var header = new SparseHeader(
            majorVersion,
            minorVersion,
            fileHeaderSize,
            chunkHeaderSize,
            blockSize,
            totalBlocks,
            totalChunks,
            imageChecksum);
        _ = header.RawLength;

        var regions = new List<SparseRegion>();
        var rawChunks = new List<SparseDataChunk>();
        uint currentBlock = 0;
        uint rawStartBlock = 0;
        long rawLength = 0;
        Span<byte> chunkHeader = stackalloc byte[SparseConstant.ChunkLength];
        Span<byte> pattern = stackalloc byte[sizeof(uint)];

        for (uint index = 0; index < totalChunks; index++)
        {
            source.ReadExactly(chunkHeader);

            ushort chunkType = BinaryPrimitives.ReadUInt16LittleEndian(chunkHeader);
            uint chunkBlocks = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[8..]);
            long outputLength = checked((long)chunkBlocks * blockSize);

            if (totalSize < chunkHeaderSize)
                throw InvalidChunk(index, Strings.LegacyChunkTotalSizeTooSmall);

            Skip(source, chunkHeaderSize - SparseConstant.ChunkLength);
            long payloadSize = totalSize - chunkHeaderSize;

            switch (chunkType)
            {
                case SparseChunkTypeConstant.Raw:
                    if (payloadSize != outputLength)
                        throw InvalidChunk(index, Strings.LegacyRawPayloadSizeMismatch);
                    if (rawChunks.Count == 0)
                        rawStartBlock = currentBlock;

                    rawChunks.Add(new SparseDataChunk(
                        SparseDataChunkType.Raw,
                        source.Position,
                        outputLength,
                        0));
                    rawLength = checked(rawLength + outputLength);
                    Skip(source, outputLength);
                    break;

                case SparseChunkTypeConstant.Fill:
                    FlushRawRegion(regions, rawChunks, rawStartBlock, ref rawLength);
                    if (payloadSize != sizeof(uint))
                        throw InvalidChunk(index, Strings.LegacyFillPayloadSizeInvalid);

                    source.ReadExactly(pattern);
                    uint fillPattern = BinaryPrimitives.ReadUInt32LittleEndian(pattern);
                    regions.Add(new SparseRegion(
                        currentBlock,
                        outputLength,
                        [new SparseDataChunk(SparseDataChunkType.Fill, 0, outputLength, fillPattern)]));
                    break;

                case SparseChunkTypeConstant.DontCare:
                    FlushRawRegion(regions, rawChunks, rawStartBlock, ref rawLength);
                    if (payloadSize != 0)
                        throw InvalidChunk(index, Strings.DontCarePayloadNotEmpty);
                    break;

                case SparseChunkTypeConstant.Crc32:
                    FlushRawRegion(regions, rawChunks, rawStartBlock, ref rawLength);
                    if (payloadSize is not 0 and not sizeof(uint))
                        throw InvalidChunk(index, Strings.LegacyCrc32PayloadInvalid);
                    Skip(source, payloadSize);
                    break;

                default:
                    throw InvalidChunk(
                        index,
                        Strings.FormatUnknownChunkType(
                            chunkType.ToString("X4", CultureInfo.InvariantCulture)));
            }

            currentBlock = checked(currentBlock + chunkBlocks);
        }

        FlushRawRegion(regions, rawChunks, rawStartBlock, ref rawLength);
        if (currentBlock != totalBlocks)
            throw new SparseException(
                Strings.FormatBlockCountMismatch(totalBlocks, currentBlock));

        return new SparseImage(header, regions);
    }

    private static void FlushRawRegion(
        List<SparseRegion> regions,
        List<SparseDataChunk> rawChunks,
        uint startBlock,
        ref long length)
    {
        if (rawChunks.Count == 0)
            return;

        regions.Add(new SparseRegion(startBlock, length, rawChunks.ToArray()));
        rawChunks.Clear();
        length = 0;
    }

    private static void Skip(Stream source, long count)
    {
        if (count < 0 || source.Position > source.Length - count)
            throw new EndOfStreamException();
        source.Seek(count, SeekOrigin.Current);
    }

    private static bool TryReadExactly(Stream source, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = source.Read(buffer[offset..]);
            if (read == 0)
                return false;
            offset += read;
        }

        return true;
    }

    private static SparseException InvalidChunk(uint index, string reason) =>
        new(Strings.FormatInvalidChunk(index, reason));
}
