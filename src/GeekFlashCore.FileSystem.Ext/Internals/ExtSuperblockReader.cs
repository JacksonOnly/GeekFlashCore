using System.Buffers.Binary;
using System.Text;
using GeekFlashCore.FileSystem.Ext.Constants;
using GeekFlashCore.FileSystem.Ext.Models;
using GeekFlashCore.FileSystem.Ext.Types;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;

namespace GeekFlashCore.FileSystem.Ext.Internals;

internal static class ExtSuperblockReader
{
    public static bool TryReadRaw(IReadableBlockDevice source, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (destination.Length < ExtFormat.SuperblockSize)
            throw new ArgumentException(Strings.DestinationTooSmall, nameof(destination));
        if (source.Length < ExtFormat.SuperblockOffset + ExtFormat.SuperblockSize)
            return false;

        try
        {
            BlockDeviceIO.ReadExactlyAt(
                source,
                ExtFormat.SuperblockOffset,
                destination[..ExtFormat.SuperblockSize]);
            return true;
        }
        catch (EndOfStreamException exception)
        {
            throw new ExtFileSystemException(Strings.IoFailure, exception);
        }
    }

    public static ExtSuperblock Parse(IReadableBlockDevice source, ReadOnlySpan<byte> data)
    {
        if (data.Length < ExtFormat.SuperblockSize)
            throw new ArgumentException(Strings.SuperblockBufferTooSmall, nameof(data));
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[56..]) != ExtFormat.Magic)
        {
            throw new ExtFileSystemException(Strings.InvalidFormat);
        }

        uint revision = ReadUInt32(data, 76);
        if (revision > 1)
            throw Corrupt("revision", 76, Strings.UnsupportedFeature);

        uint logBlockSize = ReadUInt32(data, 24);
        if (logBlockSize > 6)
            throw Corrupt("log_block_size", 24, Strings.CorruptMetadata);
        int blockSize = checked(1024 << (int)logBlockSize);

        var incompatible = (ExtIncompatibleFeatures)ReadUInt32(data, 96);
        var readOnly = (ExtReadOnlyCompatibleFeatures)ReadUInt32(data, 100);
        ulong blockCount = ReadUInt32(data, 4);
        if ((incompatible & ExtIncompatibleFeatures.Bit64) != 0)
            blockCount |= (ulong)ReadUInt32(data, 336) << 32;

        uint inodeCount = ReadUInt32(data, 0);
        uint firstDataBlock = ReadUInt32(data, 20);
        uint blocksPerGroup = ReadUInt32(data, 32);
        uint inodesPerGroup = ReadUInt32(data, 40);
        if (inodeCount == 0 || blockCount == 0 || blocksPerGroup == 0 || inodesPerGroup == 0 ||
            firstDataBlock >= blockCount)
        {
            throw Corrupt("geometry", 0, Strings.CorruptMetadata);
        }

        ushort inodeSize = revision == 0 ? (ushort)128 : ReadUInt16(data, 88);
        if (inodeSize < 128 || inodeSize > blockSize || (inodeSize & (inodeSize - 1)) != 0)
            throw Corrupt("inode_size", 88, Strings.CorruptMetadata);

        ushort descriptorSize = (incompatible & ExtIncompatibleFeatures.Bit64) != 0
            ? ReadUInt16(data, 254)
            : (ushort)32;
        if ((incompatible & ExtIncompatibleFeatures.Bit64) != 0 && descriptorSize == 0)
            descriptorSize = 64;
        if (descriptorSize < 32 || descriptorSize > blockSize || (descriptorSize & 7) != 0)
            throw Corrupt("descriptor_size", 254, Strings.CorruptMetadata);

        long declaredLength;
        try
        {
            declaredLength = checked((long)blockCount * blockSize);
        }
        catch (OverflowException exception)
        {
            throw new ExtFileSystemException(Strings.ResourceLimitExceeded, exception);
        }

        if (declaredLength > source.Length)
        {
            throw new ExtFileSystemException(Strings.CorruptMetadata);
        }

        byte[] uuid = data.Slice(104, 16).ToArray();
        ReadOnlySpan<byte> labelBytes = data.Slice(120, 16);
        int terminator = labelBytes.IndexOf((byte)0);
        if (terminator >= 0) labelBytes = labelBytes[..terminator];
        string? label = labelBytes.IsEmpty ? null : Encoding.UTF8.GetString(labelBytes);

        var result = new ExtSuperblock(
            inodeCount,
            blockCount,
            firstDataBlock,
            blockSize,
            blocksPerGroup,
            inodesPerGroup,
            inodeSize,
            descriptorSize,
            (ExtCompatibleFeatures)ReadUInt32(data, 92),
            incompatible,
            readOnly,
            uuid,
            label,
            ReadUInt16(data, 58),
            ReadUInt32(data, 624));

        long descriptorTableOffset = blockSize == 1024 ? 2L * blockSize : blockSize;
        long descriptorBytes = checked((long)result.GroupCount * descriptorSize);
        if (descriptorTableOffset > declaredLength - descriptorBytes)
            throw Corrupt("group_descriptors", descriptorTableOffset, Strings.CorruptMetadata);
        return result;
    }

    public static ulong[] GetUnsupportedFeatures(ExtSuperblock superblock)
    {
        uint unsupportedIncompatible =
            (uint)superblock.IncompatibleFeatures & ~ExtFormat.SupportedIncompatibleFeatures;
        uint unsupportedReadOnly =
            (uint)superblock.ReadOnlyCompatibleFeatures & ~ExtFormat.SupportedReadOnlyCompatibleFeatures;

        var features = new List<ulong>(8);
        AddBits(features, 1, unsupportedIncompatible);
        AddBits(features, 2, unsupportedReadOnly);
        return features.ToArray();
    }

    private static void AddBits(List<ulong> features, uint category, uint bits)
    {
        while (bits != 0)
        {
            uint bit = bits & (0u - bits);
            features.Add(((ulong)category << 32) | bit);
            bits &= ~bit;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static ExtFileSystemException Corrupt(
        string field,
        long relativeOffset,
        string message) => new(
        $"{message} (structure: superblock; field: {field}; offset: {checked(ExtFormat.SuperblockOffset + relativeOffset)})");
}
