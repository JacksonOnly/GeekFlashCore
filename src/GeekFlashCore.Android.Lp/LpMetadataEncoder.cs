using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.ImageFormats.Abstractions;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal static class LpMetadataEncoder
{
    internal static byte[] EncodeAndValidate(
        LpGeometry geometry,
        int slotNumber,
        LpFinalModel model,
        ImageReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if ((uint)slotNumber >= geometry.MetadataSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotNumber));
        }

        int metadataSize = checked((int)geometry.MetadataMaxSize);
        int headerSize = model.SourceHeader.MinorVersion < 2
            ? LpFormat.HeaderV10Size
            : LpFormat.HeaderV12Size;
        int partitionsSize = checked(model.Partitions.Length * LpFormat.PartitionEntrySize);
        int extentsSize = checked(model.Extents.Length * LpFormat.ExtentEntrySize);
        int groupsSize = checked(model.Groups.Length * LpFormat.GroupEntrySize);
        int blockDevicesSize = checked(model.BlockDevices.Length * LpFormat.BlockDeviceEntrySize);
        int tablesSize = checked(partitionsSize + extentsSize + groupsSize + blockDevicesSize);
        if (headerSize > metadataSize - tablesSize)
        {
            throw new InvalidOperationException(Resources.MetadataTooLarge);
        }

        var metadata = new byte[metadataSize];
        Span<byte> header = metadata.AsSpan(0, headerSize);
        Span<byte> tables = metadata.AsSpan(headerSize, tablesSize);
        int partitionsOffset = 0;
        int extentsOffset = partitionsOffset + partitionsSize;
        int groupsOffset = extentsOffset + extentsSize;
        int blockDevicesOffset = groupsOffset + groupsSize;

        for (int index = 0; index < model.Partitions.Length; index++)
        {
            EncodePartition(
                tables.Slice(
                    partitionsOffset + index * LpFormat.PartitionEntrySize,
                    LpFormat.PartitionEntrySize),
                model.Partitions[index]);
        }
        for (int index = 0; index < model.Extents.Length; index++)
        {
            EncodeExtent(
                tables.Slice(
                    extentsOffset + index * LpFormat.ExtentEntrySize,
                    LpFormat.ExtentEntrySize),
                model.Extents[index]);
        }
        for (int index = 0; index < model.Groups.Length; index++)
        {
            EncodeGroup(
                tables.Slice(
                    groupsOffset + index * LpFormat.GroupEntrySize,
                    LpFormat.GroupEntrySize),
                model.Groups[index]);
        }
        for (int index = 0; index < model.BlockDevices.Length; index++)
        {
            EncodeBlockDevice(
                tables.Slice(
                    blockDevicesOffset + index * LpFormat.BlockDeviceEntrySize,
                    LpFormat.BlockDeviceEntrySize),
                model.BlockDevices[index]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header, LpFormat.HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], model.SourceHeader.MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], model.SourceHeader.MinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], checked((uint)headerSize));
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], checked((uint)tablesSize));
        Span<byte> tablesChecksum = stackalloc byte[32];
        SHA256.HashData(tables, tablesChecksum);
        tablesChecksum.CopyTo(header.Slice(48, 32));
        EncodeDescriptor(
            header[80..],
            partitionsOffset,
            model.Partitions.Length,
            LpFormat.PartitionEntrySize);
        EncodeDescriptor(
            header[92..],
            extentsOffset,
            model.Extents.Length,
            LpFormat.ExtentEntrySize);
        EncodeDescriptor(
            header[104..],
            groupsOffset,
            model.Groups.Length,
            LpFormat.GroupEntrySize);
        EncodeDescriptor(
            header[116..],
            blockDevicesOffset,
            model.BlockDevices.Length,
            LpFormat.BlockDeviceEntrySize);
        if (headerSize == LpFormat.HeaderV12Size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[128..], model.SourceHeader.Flags);
        }
        Span<byte> headerChecksum = stackalloc byte[32];
        SHA256.HashData(header, headerChecksum);
        headerChecksum.CopyTo(header.Slice(12, 32));

        ValidateRoundTrip(geometry, slotNumber, metadata, model, limits);
        return metadata;
    }

    private static void EncodePartition(Span<byte> destination, LpPartition partition)
    {
        EncodeName(destination[..36], partition.RawName);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[36..], (uint)partition.Attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], partition.FirstExtentIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..], partition.ExtentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[48..], partition.GroupIndex);
    }

    private static void EncodeExtent(Span<byte> destination, LpExtent extent)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, extent.SectorCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)extent.TargetType);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], extent.TargetData);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], extent.TargetSource);
    }

    private static void EncodeGroup(Span<byte> destination, LpPartitionGroup group)
    {
        EncodeName(destination[..36], group.RawName);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[36..], (uint)group.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[40..], group.MaximumSize);
    }

    private static void EncodeBlockDevice(Span<byte> destination, LpBlockDevice device)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, device.FirstLogicalSector);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], device.Alignment);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], device.AlignmentOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], device.Size);
        EncodeName(destination.Slice(24, 36), device.RawPartitionName);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[60..], (uint)device.Flags);
    }

    private static void EncodeDescriptor(
        Span<byte> destination,
        int offset,
        int count,
        int entrySize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, checked((uint)offset));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], checked((uint)count));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], checked((uint)entrySize));
    }

    private static void EncodeName(Span<byte> destination, string name)
    {
        LpNameValidator.Validate(name, nameof(name));
        destination.Clear();
        int written = Encoding.ASCII.GetBytes(name, destination);
        if (written != name.Length)
        {
            throw new InvalidDataException(Resources.FormatInvalidSourceNameEncoding(name));
        }
    }

    private static void ValidateRoundTrip(
        LpGeometry geometry,
        int slotNumber,
        byte[] metadata,
        LpFinalModel expected,
        ImageReadLimits limits)
    {
        using var device = new EncodedMetadataBlockDevice(geometry, slotNumber, metadata);
        using LpMetadataSet set = LpMetadataSet.Open(
            device,
            DeviceOwnership.Borrow,
            limits);
        using LpMetadataDocument document = set.OpenPreferredSlot(slotNumber);
        if (document.Header.MajorVersion != expected.SourceHeader.MajorVersion ||
            document.Header.MinorVersion != expected.SourceHeader.MinorVersion ||
            document.Header.Flags != expected.SourceHeader.Flags ||
            !document.Partitions.Span.SequenceEqual(expected.Partitions) ||
            !document.Extents.Span.SequenceEqual(expected.Extents) ||
            !document.Groups.Span.SequenceEqual(expected.Groups) ||
            !document.BlockDevices.Span.SequenceEqual(expected.BlockDevices))
        {
            throw new InvalidDataException(Resources.MetadataVerificationFailed);
        }
    }

    private sealed class EncodedMetadataBlockDevice : IReadableBlockDevice
    {
        private readonly LpGeometry _geometry;
        private readonly int _slotNumber;
        private readonly byte[] _metadata;
        private readonly byte[] _geometryBlock;
        private bool _disposed;

        internal EncodedMetadataBlockDevice(
            LpGeometry geometry,
            int slotNumber,
            byte[] metadata)
        {
            _geometry = geometry;
            _slotNumber = slotNumber;
            _metadata = metadata;
            _geometryBlock = EncodeGeometry(geometry);
            Length = checked(
                LpFormat.ReservedBytes +
                2L * LpFormat.GeometryBlockSize +
                2L * geometry.MetadataMaxSize * geometry.MetadataSlotCount);
        }

        public BlockDeviceId Id { get; } = new("memory:lp-encoded-metadata");
        public long Length { get; }
        public int LogicalBlockSize => LpFormat.SectorSize;

        public int ReadAt(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= Length || destination.IsEmpty)
            {
                return 0;
            }

            int length = (int)Math.Min(destination.Length, Length - offset);
            Span<byte> output = destination[..length];
            output.Clear();
            CopyOverlap(
                offset,
                output,
                LpFormat.ReservedBytes,
                _geometryBlock);
            CopyOverlap(
                offset,
                output,
                LpFormat.ReservedBytes + LpFormat.GeometryBlockSize,
                _geometryBlock);
            CopyOverlap(
                offset,
                output,
                LpMetadataSet.GetMetadataOffset(
                    _geometry,
                    _slotNumber,
                    LpMetadataCopyKind.Primary),
                _metadata);
            CopyOverlap(
                offset,
                output,
                LpMetadataSet.GetMetadataOffset(
                    _geometry,
                    _slotNumber,
                    LpMetadataCopyKind.Backup),
                _metadata);
            return length;
        }

        public void Dispose() => _disposed = true;

        private static void CopyOverlap(
            long readOffset,
            Span<byte> output,
            long dataOffset,
            ReadOnlySpan<byte> data)
        {
            long readEnd = checked(readOffset + output.Length);
            long dataEnd = checked(dataOffset + data.Length);
            long start = Math.Max(readOffset, dataOffset);
            long end = Math.Min(readEnd, dataEnd);
            if (start >= end)
            {
                return;
            }

            data.Slice(checked((int)(start - dataOffset)), checked((int)(end - start)))
                .CopyTo(output.Slice(checked((int)(start - readOffset))));
        }

        private static byte[] EncodeGeometry(LpGeometry geometry)
        {
            var block = new byte[LpFormat.GeometryBlockSize];
            Span<byte> bytes = block;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, LpFormat.GeometryMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], LpFormat.GeometryStructSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..], geometry.MetadataMaxSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..], geometry.MetadataSlotCount);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[48..], geometry.LogicalBlockSize);
            Span<byte> checksum = stackalloc byte[32];
            SHA256.HashData(bytes[..LpFormat.GeometryStructSize], checksum);
            checksum.CopyTo(bytes.Slice(8, 32));
            return block;
        }
    }
}
