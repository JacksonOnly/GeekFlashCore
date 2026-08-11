using System.Buffers.Binary;
using System.Globalization;
using GeekFlashCore.Android.Sparse.Constants;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.Android.Sparse.Types;

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
