using System.Buffers.Binary;
using GeekFlashCore.FileSystem.Erofs.Models;
using GeekFlashCore.FileSystem.Erofs.Types;

namespace GeekFlashCore.FileSystem.Erofs.Internals;

internal sealed class ErofsCompressionMapper
{
    private const ushort Compacted2B = 0x0001;
    private const ushort BigPcluster1 = 0x0002;
    private const ushort BigPcluster2 = 0x0004;
    private const ushort InlinePcluster = 0x0008;
    private const ushort InterlacedPcluster = 0x0010;
    private const ushort FragmentPcluster = 0x0020;
    private const ushort ClusterTypeMask = 0x0003;
    private const ushort PartialReference = 0x8000;
    private const ushort CompressedBlockCount = 1 << 11;

    private readonly ErofsVolume _volume;
    private readonly ErofsInode _inode;
    private readonly string _objectId;
    private readonly long _headerOffset;
    private readonly ushort _advise;
    private readonly ushort _inlineDataSize;
    private readonly long _inlineTailOffset;
    private readonly int _logicalClusterBits;
    private readonly byte _algorithm1;
    private readonly byte _algorithm2;

    public ErofsCompressionMapper(ErofsVolume volume, ErofsInode inode)
    {
        _volume = volume;
        _inode = inode;
        _objectId = $"nid:{inode.NodeId}";
        long metadataEnd = checked(inode.DiskOffset + inode.InodeSize + inode.XattrSize);
        _headerOffset = AlignUp(metadataEnd, 8);
        Span<byte> header = stackalloc byte[8];
        volume.ReadExactlyAt(_headerOffset, header, "compression_header", _objectId);
        ulong rawHeader = BinaryPrimitives.ReadUInt64LittleEndian(header);
        if ((rawHeader & (1UL << 63)) != 0)
            throw Unsupported(
                Strings.UnsupportedFeature,
                (ulong)ErofsIncompatibleFeatures.Fragments);

        _advise = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        _inlineDataSize = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        if ((_advise & FragmentPcluster) != 0)
            throw Unsupported(
                Strings.UnsupportedFeature,
                (ulong)ErofsIncompatibleFeatures.Fragments);
        if ((_advise & InlinePcluster) != 0 &&
            (volume.Superblock.IncompatibleFeatures &
             ErofsIncompatibleFeatures.CompressedTailPacking) == 0)
        {
            throw Corrupt("advise", Strings.CorruptMetadata);
        }
        if (inode.DataLayout == ErofsDataLayout.CompressedFull && (_advise & 1) != 0)
            throw Unsupported(Strings.CorruptMetadata, 0x100000001UL);

        _logicalClusterBits = volume.Superblock.BlockSizeBits + (header[7] & 0x0F);
        if (_logicalClusterBits > 31 || _logicalClusterBits < volume.Superblock.BlockSizeBits)
            throw Corrupt("cluster_bits", Strings.CorruptMetadata);
        if (inode.DataLayout == ErofsDataLayout.CompressedCompact && _logicalClusterBits > 14)
            throw Unsupported(Strings.UnsupportedFeature, (ulong)_logicalClusterBits);
        _algorithm1 = (byte)(header[6] & 0x0F);
        _algorithm2 = (byte)(header[6] >> 4);
        if (_algorithm1 >= 4 || _algorithm2 >= 4)
            throw Unsupported(Strings.UnsupportedFeature, _algorithm1 >= 4 ? _algorithm1 : _algorithm2);
        if (inode.DataLayout == ErofsDataLayout.CompressedCompact &&
            ((_advise & BigPcluster1) != 0) != ((_advise & BigPcluster2) != 0))
            throw Corrupt("advise", Strings.CorruptMetadata);
        if ((_advise & InlinePcluster) != 0)
        {
            if (inode.Size == 0)
                throw Corrupt("inline_size", Strings.CorruptMetadata);
            ulong tailLcn = (inode.Size - 1) >> _logicalClusterBits;
            _inlineTailOffset = Load(tailLcn, false).NextPackOffset;
        }
    }

    public ErofsCompressionExtent Map(ulong logicalOffset)
    {
        if (logicalOffset >= _inode.Size) throw new ArgumentOutOfRangeException(nameof(logicalOffset));
        ulong initialLcn = logicalOffset >> _logicalClusterBits;
        uint clusterMask = (1u << _logicalClusterBits) - 1;
        uint endOffset = (uint)logicalOffset & clusterMask;
        ClusterRecord record = Load(initialLcn, false);
        ulong logicalEnd = checked((record.Lcn + 1) << _logicalClusterBits);
        ClusterRecord head;
        int headType;
        ulong logicalStart;

        if (record.Type is 0 or 1 or 3)
        {
            if (endOffset >= record.ClusterOffset)
            {
                head = record;
                headType = record.Type;
                logicalStart = checked((record.Lcn << _logicalClusterBits) | record.ClusterOffset);
            }
            else
            {
                if (record.Lcn == 0)
                    throw Corrupt("cluster_offset", Strings.CorruptMetadata);
                logicalEnd = checked((record.Lcn << _logicalClusterBits) | record.ClusterOffset);
                head = LookBack(record.Lcn, 1);
                headType = head.Type;
                logicalStart = checked((head.Lcn << _logicalClusterBits) | head.ClusterOffset);
            }
        }
        else if (record.Type == 2)
        {
            head = LookBack(record.Lcn, record.Delta0);
            headType = head.Type;
            logicalStart = checked((head.Lcn << _logicalClusterBits) | head.ClusterOffset);
        }
        else
        {
            throw Corrupt("cluster_type", Strings.CorruptMetadata);
        }

        if (logicalOffset < logicalStart || logicalOffset >= logicalEnd)
            throw Corrupt("logical_range", Strings.CorruptMetadata);
        ulong decodedEnd = FindDecodedEnd(head, logicalStart);
        if (decodedEnd <= logicalStart || decodedEnd > _inode.Size)
            throw Corrupt("decoded_length", Strings.CorruptMetadata);
        ulong decodedLength = decodedEnd - logicalStart;

        ulong physicalOffset;
        uint encodedLength;
        bool inlineTail = (_advise & InlinePcluster) != 0 && decodedEnd == _inode.Size;
        if (inlineTail)
        {
            encodedLength = _inlineDataSize;
            if (encodedLength == 0)
                throw Corrupt("inline_size", Strings.CorruptMetadata);
            long inlineOffset = _inlineTailOffset;
            int offsetInBlock = (int)(inlineOffset & (_volume.Superblock.BlockSize - 1));
            if (inlineOffset < 0 ||
                inlineOffset > _volume.Superblock.DeclaredLength - encodedLength ||
                encodedLength > _volume.Superblock.BlockSize - offsetInBlock)
            {
                throw Corrupt("inline_range", Strings.CorruptMetadata);
            }
            physicalOffset = (ulong)inlineOffset;
        }
        else
        {
            uint compressedBlocks = GetCompressedBlockCount(head, headType);
            if (compressedBlocks == 0)
                throw Corrupt("compressed_blocks", Strings.CorruptMetadata);
            ulong physicalBlock = head.PhysicalBlock;
            if (physicalBlock >= _volume.Superblock.BlockCount ||
                compressedBlocks > _volume.Superblock.BlockCount - physicalBlock)
                throw Corrupt("physical_block", Strings.CorruptMetadata);
            ulong encodedLength64 = (ulong)compressedBlocks * (uint)_volume.Superblock.BlockSize;
            if (encodedLength64 > int.MaxValue)
                throw ResourceLimit(logicalOffset, Strings.ResourceLimitExceeded);
            encodedLength = (uint)encodedLength64;
            physicalOffset = physicalBlock * (uint)_volume.Superblock.BlockSize;
        }

        if (encodedLength > _volume.Limits.MaximumCompressedInputBytes ||
            decodedLength > (ulong)_volume.Limits.MaximumDecodedBytes)
        {
            throw ResourceLimit(
                logicalOffset,
                Strings.ResourceLimitExceeded);
        }

        ErofsCompressionAlgorithm algorithm;
        if (headType == 0)
        {
            if (decodedLength > encodedLength)
                throw Corrupt("plain_length", Strings.CorruptMetadata);
            algorithm = (_advise & InterlacedPcluster) != 0
                ? ErofsCompressionAlgorithm.Interlaced
                : ErofsCompressionAlgorithm.Shifted;
        }
        else
        {
            byte algorithmId = headType == 3 ? _algorithm2 : _algorithm1;
            if ((_volume.Superblock.AvailableCompressionAlgorithms & (1 << algorithmId)) == 0)
                throw Corrupt("algorithm", Strings.UnsupportedFeature);
            algorithm = (ErofsCompressionAlgorithm)algorithmId;
        }

        return new ErofsCompressionExtent(
            logicalStart,
            decodedLength,
            physicalOffset,
            encodedLength,
            algorithm,
            head.PartialReference);
    }

    private ClusterRecord Load(ulong logicalCluster, bool lookAhead)
    {
        ulong totalIndexes = (_inode.Size + (uint)_volume.Superblock.BlockSize - 1) /
            (uint)_volume.Superblock.BlockSize;
        if (logicalCluster >= totalIndexes)
            throw Corrupt("logical_cluster", Strings.CorruptMetadata);
        return _inode.DataLayout == ErofsDataLayout.CompressedFull
            ? LoadFull(logicalCluster)
            : LoadCompact(logicalCluster, lookAhead, totalIndexes);
    }

    private ClusterRecord LoadFull(ulong lcn)
    {
        long indexOffset = checked(_headerOffset + 16 + checked((long)lcn * 8));
        Span<byte> raw = stackalloc byte[8];
        _volume.ReadExactlyAt(indexOffset, raw, "compression_index", _objectId);
        ushort advise = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        int type = advise & ClusterTypeMask;
        if (type == 2)
        {
            ushort delta0 = BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]);
            uint compressedBlocks = 0;
            if ((delta0 & CompressedBlockCount) != 0)
            {
                if ((_advise & (BigPcluster1 | BigPcluster2)) == 0)
                    throw Corrupt("compressed_blocks", Strings.CorruptMetadata);
                compressedBlocks = (uint)(delta0 & ~CompressedBlockCount);
                delta0 = 1;
            }
            return new ClusterRecord(
                lcn,
                type,
                1u << _logicalClusterBits,
                delta0,
                BinaryPrimitives.ReadUInt16LittleEndian(raw[6..]),
                0,
                compressedBlocks,
                false,
                indexOffset + 8);
        }

        uint clusterOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw[2..]);
        if (clusterOffset >= 1u << _logicalClusterBits)
            throw Corrupt("cluster_offset", Strings.CorruptMetadata);
        return new ClusterRecord(
            lcn,
            type,
            clusterOffset,
            0,
            0,
            BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
            0,
            (advise & PartialReference) != 0,
            indexOffset + 8);
    }

    private ClusterRecord LoadCompact(ulong originalLcn, bool lookAhead, ulong totalIndexes)
    {
        long ebase = checked(_headerOffset + 8);
        ulong initial4 = (ulong)(((32 - (ebase & 31)) / 4) & 7);
        ulong central2 = 0;
        if ((_advise & Compacted2B) != 0 && initial4 < totalIndexes)
            central2 = (totalIndexes - initial4) & ~15UL;

        ulong relativeLcn = originalLcn;
        long position = ebase;
        int itemSize = 4;
        if (relativeLcn >= initial4)
        {
            position = checked(position + checked((long)initial4 * 4));
            relativeLcn -= initial4;
            if (relativeLcn < central2)
            {
                itemSize = 2;
            }
            else
            {
                position = checked(position + checked((long)central2 * 2));
                relativeLcn -= central2;
            }
        }
        position = checked(position + checked((long)relativeLcn * itemSize));

        int itemCount = itemSize == 4 ? 2 : 16;
        if (itemSize == 2 && _logicalClusterBits > 12)
            throw Unsupported(Strings.UnsupportedFeature, (ulong)_logicalClusterBits);
        int packSize = itemCount * itemSize;
        long packOffset = position & ~(packSize - 1L);
        int itemIndex = (int)((position - packOffset) / itemSize);
        Span<byte> pack = stackalloc byte[32];
        _volume.ReadExactlyAt(
            packOffset,
            pack[..packSize],
            "compression_index",
            _objectId);
        int logicalBits = Math.Max(_logicalClusterBits, 12);
        int encodedBits = ((packSize - 4) * 8) >>
            System.Numerics.BitOperations.Log2((uint)itemCount);
        (uint low, int type) = DecodePacked(pack[..packSize], logicalBits, encodedBits * itemIndex);
        if (type == 2)
        {
            uint delta1 = lookAhead
                ? GetLookAheadDistance(pack[..packSize], logicalBits, encodedBits, itemCount, itemIndex)
                : 0;
            uint compressedBlocks = 0;
            uint delta0;
            if ((low & CompressedBlockCount) != 0)
            {
                if ((_advise & BigPcluster1) == 0)
                    throw Corrupt("compressed_blocks", Strings.CorruptMetadata);
                compressedBlocks = low & ~(uint)CompressedBlockCount;
                delta0 = 1;
            }
            else if (itemIndex + 1 != itemCount)
            {
                delta0 = low;
            }
            else
            {
                (uint previousLow, int previousType) = DecodePacked(
                    pack[..packSize],
                    logicalBits,
                    encodedBits * (itemIndex - 1));
                delta0 = previousType != 2 ? 1u : (previousLow & CompressedBlockCount) != 0 ? 2u : previousLow + 1;
            }
            return new ClusterRecord(
                originalLcn,
                type,
                1u << _logicalClusterBits,
                delta0,
                delta1,
                0,
                compressedBlocks,
                false,
                packOffset + packSize);
        }

        uint precedingBlocks = CalculatePrecedingPhysicalBlocks(
            pack[..packSize],
            logicalBits,
            encodedBits,
            itemIndex,
            (_advise & BigPcluster1) != 0);
        uint physicalBase = BinaryPrimitives.ReadUInt32LittleEndian(pack.Slice(packSize - 4));
        return new ClusterRecord(
            originalLcn,
            type,
            low,
            0,
            0,
            checked((ulong)physicalBase + precedingBlocks),
            0,
            false,
            packOffset + packSize);
    }

    private ClusterRecord LookBack(ulong lcn, uint distance)
    {
        int remaining = _volume.Limits.MaximumMappingDepth * 1024;
        while (distance != 0 && lcn >= distance && remaining-- > 0)
        {
            lcn -= distance;
            ClusterRecord record = Load(lcn, false);
            if (record.Type == 2)
            {
                distance = record.Delta0;
                continue;
            }
            if (record.Type is 0 or 1 or 3) return record;
            break;
        }
        throw Corrupt("lookback", Strings.CorruptMetadata);
    }

    private ulong FindDecodedEnd(ClusterRecord head, ulong logicalStart)
    {
        ulong lcn = head.Lcn;
        ulong headLcn = logicalStart >> _logicalClusterBits;
        ulong maximumEnd = logicalStart + (uint)_volume.Limits.MaximumDecodedBytes;
        while (true)
        {
            ulong clusterStart = lcn << _logicalClusterBits;
            if (clusterStart >= _inode.Size)
            {
                if (_inode.Size > maximumEnd)
                    throw ResourceLimit(logicalStart, Strings.ResourceLimitExceeded);
                return _inode.Size;
            }
            if (clusterStart > maximumEnd)
                throw ResourceLimit(logicalStart, Strings.ResourceLimitExceeded);

            ClusterRecord record = Load(lcn, true);
            uint distance;
            if (record.Type == 2)
            {
                distance = record.Delta1 == 0 ? 1u : record.Delta1;
            }
            else if (record.Type is 0 or 1 or 3)
            {
                if (lcn != headLcn)
                {
                    ulong end = checked((lcn << _logicalClusterBits) + record.ClusterOffset);
                    end = Math.Min(end, _inode.Size);
                    if (end > maximumEnd)
                        throw ResourceLimit(logicalStart, Strings.ResourceLimitExceeded);
                    return end;
                }
                distance = 1;
            }
            else
            {
                throw Corrupt("cluster_type", Strings.CorruptMetadata);
            }
            lcn = checked(lcn + distance);
        }
    }

    private uint GetCompressedBlockCount(ClusterRecord head, int headType)
    {
        bool big1 = (_advise & BigPcluster1) != 0;
        bool big2 = (_advise & BigPcluster2) != 0;
        if ((headType == 1 && !big1) || ((headType == 0 || headType == 3) && !big2) ||
            ((head.Lcn + 1) << _logicalClusterBits) >= _inode.Size)
            return 1;
        if (head.CompressedBlocks != 0) return head.CompressedBlocks;

        ClusterRecord next = Load(head.Lcn + 1, false);
        if (next.Type == 2)
        {
            if (next.Delta0 != 1)
                throw Corrupt("compressed_blocks", Strings.CorruptMetadata);
            if (next.CompressedBlocks != 0) return next.CompressedBlocks;
        }
        else if (next.Type is 0 or 1 or 3)
        {
            return 1;
        }
        throw Corrupt("compressed_blocks", Strings.CorruptMetadata);
    }

    private uint CalculatePrecedingPhysicalBlocks(
        ReadOnlySpan<byte> pack,
        int logicalBits,
        int encodedBits,
        int itemIndex,
        bool bigPcluster)
    {
        int index = itemIndex;
        uint blocks = bigPcluster ? 0u : 1u;
        while (index > 0)
        {
            index--;
            (uint low, int type) = DecodePacked(pack, logicalBits, encodedBits * index);
            if (!bigPcluster)
            {
                if (type == 2) index -= checked((int)low);
                if (index >= 0) blocks++;
                continue;
            }

            if (type == 2)
            {
                if ((low & CompressedBlockCount) != 0)
                {
                    index--;
                    blocks = checked(blocks + (low & ~(uint)CompressedBlockCount));
                    continue;
                }
                if (low <= 1)
                    throw Corrupt("delta0", Strings.CorruptMetadata);
                index -= checked((int)low - 2);
                continue;
            }

            blocks++;
        }
        return blocks;
    }

    private uint GetLookAheadDistance(
        ReadOnlySpan<byte> pack,
        int logicalBits,
        int encodedBits,
        int itemCount,
        int itemIndex)
    {
        uint distance = 0;
        uint low = 0;
        int type;
        do
        {
            (low, type) = DecodePacked(pack, logicalBits, encodedBits * itemIndex);
            if (type != 2) return distance;
            distance++;
        }
        while (++itemIndex < itemCount);
        if ((low & CompressedBlockCount) == 0)
        {
            if (low == 0)
                throw Corrupt("delta1", Strings.CorruptMetadata);
            distance = checked(distance + low - 1);
        }
        return distance;
    }

    private static (uint Low, int Type) DecodePacked(ReadOnlySpan<byte> pack, int logicalBits, int bitPosition)
    {
        int byteOffset = bitPosition >> 3;
        int bitOffset = bitPosition & 7;
        if (byteOffset > pack.Length - 4) throw new InvalidDataException(Strings.CorruptMetadata);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(pack[byteOffset..]) >> bitOffset;
        uint mask = (1u << logicalBits) - 1;
        return (value & mask, (int)(value >> logicalBits) & 3);
    }

    private ErofsFileSystemException Corrupt(string field, string message) =>
        _volume.Corrupt(
            "compression_index",
            field,
            _headerOffset,
            message,
            $"nid:{_inode.NodeId}");

    private ErofsFileSystemException Unsupported(string message, ulong feature) =>
        _volume.Unsupported(
            "compression_header",
            "advise",
            _headerOffset,
            message,
            $"nid:{_inode.NodeId}",
            feature);

    private ErofsFileSystemException ResourceLimit(ulong logicalOffset, string message) => new(
        $"{message} (structure: compression_extent; object: nid:{_inode.NodeId}/logical:{logicalOffset})");

    private static long AlignUp(long value, int alignment) =>
        checked((value + alignment - 1) & -alignment);

    private readonly record struct ClusterRecord(
        ulong Lcn,
        int Type,
        uint ClusterOffset,
        uint Delta0,
        uint Delta1,
        ulong PhysicalBlock,
        uint CompressedBlocks,
        bool PartialReference,
        long NextPackOffset);
}
