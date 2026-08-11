using System.Buffers.Binary;
using System.Text;
using GeekFlashCore.FileSystem.Erofs.Constants;
using GeekFlashCore.FileSystem.Erofs.Models;
using GeekFlashCore.FileSystem.Erofs.Types;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.FileSystem.Erofs.Internals;

internal static class ErofsSuperblockReader
{
    public static bool TryReadRaw(IReadableBlockDevice source, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (destination.Length < ErofsFormat.SuperblockStructureSize)
            throw new ArgumentException(Strings.SuperblockBufferTooSmall, nameof(destination));
        if (source.Length < ErofsFormat.SuperblockOffset + ErofsFormat.SuperblockStructureSize)
            return false;
        try
        {
            BlockDeviceIO.ReadExactlyAt(
                source,
                ErofsFormat.SuperblockOffset,
                destination[..ErofsFormat.SuperblockStructureSize]);
            return true;
        }
        catch (EndOfStreamException exception)
        {
            throw new ErofsFileSystemException(Strings.IoFailure, exception);
        }
    }

    public static ErofsSuperblock Parse(IReadableBlockDevice source, ReadOnlySpan<byte> raw)
    {
        if (raw.Length < ErofsFormat.SuperblockStructureSize)
            throw new ArgumentException(Strings.SuperblockBufferTooSmall, nameof(raw));
        if (ReadUInt32(raw, 0) != ErofsFormat.Magic)
            throw Invalid("magic", 0, Strings.InvalidFormat);

        byte blockBits = raw[12];
        if (blockBits is < 9 or > 16)
            throw Invalid("block_size", 12, Strings.CorruptMetadata);
        int blockSize = 1 << blockBits;
        int superblockSize = checked(128 + raw[13] * 16);
        if (superblockSize > blockSize || ErofsFormat.SuperblockOffset > source.Length - superblockSize)
            throw Invalid("extension_slots", 13, Strings.CorruptMetadata);

        uint incompatibleValue = ReadUInt32(raw, 80);
        var incompatible = (ErofsIncompatibleFeatures)incompatibleValue;
        var compatible = (ErofsCompatibleFeatures)ReadUInt32(raw, 8);
        ulong blockCount = ReadUInt32(raw, 36);
        ulong rootNodeId = ReadUInt16(raw, 14);
        if ((incompatible & ErofsIncompatibleFeatures.Bit48) != 0 && ReadUInt64(raw, 112) != 0)
        {
            blockCount |= (ulong)ReadUInt16(raw, 14) << 32;
            rootNodeId = ReadUInt64(raw, 112);
        }
        if (blockCount == 0 || rootNodeId == 0 || blockCount > (ulong)long.MaxValue / (uint)blockSize)
            throw Invalid("geometry", 14, Strings.CorruptMetadata);
        long declaredLength = checked((long)blockCount * blockSize);
        if (declaredLength > source.Length)
            throw Invalid("blocks", 36, Strings.CorruptMetadata);

        ulong metadataBlock = ReadUInt32(raw, 40);
        ulong xattrBlock = ReadUInt32(raw, 44);
        if (metadataBlock >= blockCount ||
            ((compatible & ErofsCompatibleFeatures.SharedXattrsInMetabox) == 0 &&
             xattrBlock >= blockCount))
            throw Invalid("metadata_blocks", 40, Strings.CorruptMetadata);

        ReadOnlySpan<byte> labelBytes = raw.Slice(64, 16);
        int terminator = labelBytes.IndexOf((byte)0);
        if (terminator >= 0) labelBytes = labelBytes[..terminator];
        string? label = labelBytes.IsEmpty ? null : Encoding.UTF8.GetString(labelBytes);
        byte[] uuid = raw.Slice(48, 16).ToArray();
        ushort algorithms = (incompatible & ErofsIncompatibleFeatures.CompressionConfigurations) != 0
            ? ReadUInt16(raw, 84)
            : (ushort)1;
        byte xattrPrefixCount = raw[91];
        byte ishareXattrPrefixId = raw[105];
        ulong metaboxNodeId = 0;
        if ((incompatible & ErofsIncompatibleFeatures.Metabox) != 0)
        {
            if (superblockSize < 136)
                throw Invalid("metabox_nid", 128, Strings.CorruptMetadata);
            metaboxNodeId = ReadUInt64(raw, 128);
            if ((metaboxNodeId & ~ErofsFormat.NodeIdMask) != 0)
                throw Invalid("metabox_nid", 128, Strings.CorruptMetadata);
        }
        if ((compatible & ErofsCompatibleFeatures.IshareXattrs) != 0 &&
            ishareXattrPrefixId >= xattrPrefixCount)
        {
            throw Invalid("ishare_xattr_prefix_id", 105, Strings.CorruptMetadata);
        }

        var result = new ErofsSuperblock(
            compatible,
            incompatible,
            blockSize,
            raw[13],
            rootNodeId,
            ReadUInt64(raw, 16),
            unchecked((long)ReadUInt64(raw, 24)),
            ReadUInt32(raw, 32),
            blockCount,
            metadataBlock,
            xattrBlock,
            uuid,
            label,
            algorithms,
            ReadUInt16(raw, 86),
            ReadUInt16(raw, 88),
            xattrPrefixCount,
            ReadUInt32(raw, 92),
            ReadUInt64(raw, 96),
            ishareXattrPrefixId,
            metaboxNodeId,
            ReadUInt32(raw, 4));

        bool metaboxRoot = (rootNodeId & ~ErofsFormat.NodeIdMask) != 0;
        if (metaboxRoot && (incompatible & ErofsIncompatibleFeatures.Metabox) == 0)
            throw Invalid("root_nid", 14, Strings.CorruptMetadata);
        if (!metaboxRoot)
        {
            long rootOffset = GetInodeOffset(result, rootNodeId);
            if (rootOffset < 0 || rootOffset > declaredLength - 32)
                throw Invalid("root_nid", 14, Strings.CorruptMetadata);
        }
        return result;
    }

    public static void VerifyChecksum(IReadableBlockDevice source, ErofsSuperblock superblock)
    {
        if ((superblock.CompatibleFeatures & ErofsCompatibleFeatures.SuperblockChecksum) == 0)
            return;

        int length = superblock.BlockSize > ErofsFormat.SuperblockOffset
            ? superblock.BlockSize - ErofsFormat.SuperblockOffset
            : superblock.BlockSize;
        Span<byte> buffer = stackalloc byte[4096];
        uint crc = uint.MaxValue;
        int completed = 0;
        while (completed < length)
        {
            int count = Math.Min(buffer.Length, length - completed);
            Span<byte> chunk = buffer[..count];
            try
            {
                BlockDeviceIO.ReadExactlyAt(source, ErofsFormat.SuperblockOffset + completed, chunk);
            }
            catch (EndOfStreamException exception)
            {
                throw new ErofsFileSystemException(Strings.ChecksumMismatch, exception);
            }

            int checksumStart = 4 - completed;
            if (checksumStart < count && checksumStart + 4 > 0)
            {
                int start = Math.Max(0, checksumStart);
                int end = Math.Min(count, checksumStart + 4);
                chunk[start..end].Clear();
            }
            crc = ErofsCrc32C.Update(crc, chunk);
            completed += count;
        }

        if (crc != superblock.StoredChecksum)
        {
            throw new ErofsFileSystemException(Strings.ChecksumMismatch);
        }
    }

    public static ulong GetUnsupportedFeature(ErofsSuperblock superblock) =>
        ((uint)superblock.IncompatibleFeatures & ~ErofsFormat.KnownIncompatibleFeatures) |
        (superblock.ExtraDevices == 0 ? 0UL : (ulong)ErofsIncompatibleFeatures.DeviceTable) |
        ((superblock.RootNodeId & ~ErofsFormat.NodeIdMask) == 0
            ? 0UL
            : (ulong)ErofsIncompatibleFeatures.Metabox);

    public static long GetInodeOffset(ErofsSuperblock superblock, ulong nodeId)
    {
        if ((nodeId & ~ErofsFormat.NodeIdMask) != 0) return -1;
        try
        {
            return checked(
                checked((long)superblock.MetadataBlock * superblock.BlockSize) +
                checked((long)nodeId << ErofsFormat.InodeSlotBits));
        }
        catch (OverflowException)
        {
            return -1;
        }
    }

    private static ErofsFileSystemException Invalid(
        string field,
        int relativeOffset,
        string message) => new(
        $"{message} (structure: superblock; field: {field}; offset: {checked(ErofsFormat.SuperblockOffset + relativeOffset)})");

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
    private static ulong ReadUInt64(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
}
