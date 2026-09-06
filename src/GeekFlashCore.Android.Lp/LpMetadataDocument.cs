using System.Buffers.Binary;
using System.Text;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.Android.Lp.Localization;
using GeekFlashCore.ImageFormats.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

public sealed class LpMetadataDocument : IDisposable
{
    private readonly LpMetadataSet _set;
    private readonly ByteBudgetLease _reservation;
    private readonly FormatLifetime _lifetime = new();
    private readonly LpPartition[] _partitions;
    private readonly LpExtent[] _extents;
    private readonly LpPartitionGroup[] _groups;
    private readonly LpBlockDevice[] _blockDevices;

    private LpMetadataDocument(
        LpMetadataSet set,
        LpMetadataCopySummary summary,
        ByteBudgetLease reservation,
        LpPartition[] partitions,
        LpExtent[] extents,
        LpPartitionGroup[] groups,
        LpBlockDevice[] blockDevices)
    {
        _set = set;
        _reservation = reservation;
        _partitions = partitions;
        _extents = extents;
        _groups = groups;
        _blockDevices = blockDevices;
        Summary = summary;
        Header = summary.Header!;
    }

    public LpMetadataCopySummary Summary { get; }
    public LpMetadataHeader Header { get; }
    public ReadOnlyMemory<LpPartition> Partitions => _partitions;
    public ReadOnlyMemory<LpExtent> Extents => _extents;
    public ReadOnlyMemory<LpPartitionGroup> Groups => _groups;
    public ReadOnlyMemory<LpBlockDevice> BlockDevices => _blockDevices;

    internal static LpMetadataDocument Parse(
        LpMetadataSet set,
        LpMetadataCopySummary summary,
        ByteBudgetLease reservation)
    {
        set.ThrowIfDisposed();
        LpMetadataHeader header = summary.Header!;
        long tablesOffset = checked(summary.Offset + header.HeaderSize);
        int slot = summary.SlotNumber;
        string suffix = $"_{(char)('a' + slot)}";

        var partitions = new LpPartition[checked((int)header.Partitions.EntryCount)];
        var extents = new LpExtent[checked((int)header.Extents.EntryCount)];
        var groups = new LpPartitionGroup[checked((int)header.Groups.EntryCount)];
        var blockDevices = new LpBlockDevice[checked((int)header.BlockDevices.EntryCount)];
        Span<byte> entry = stackalloc byte[LpFormat.BlockDeviceEntrySize];

        for (int index = 0; index < partitions.Length; index++)
        {
            ReadEntry(set.Source, tablesOffset, header.Partitions, index, entry[..LpFormat.PartitionEntrySize]);
            string rawName = ReadName(entry[..36]);
            var attributes = (LpPartitionAttributes)BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]);
            uint validMask = (uint)(LpPartitionAttributes.ReadOnly | LpPartitionAttributes.SlotSuffixed);
            if (header.MinorVersion >= 1)
                validMask |= (uint)(LpPartitionAttributes.Updated | LpPartitionAttributes.Disabled);
            if (((uint)attributes & ~validMask) != 0)
                throw Invalid(set, summary, "partition", index, Resources.Errors_Lp_PartitionAttributesUnknown);
            string name = (attributes & LpPartitionAttributes.SlotSuffixed) != 0
                ? rawName + suffix
                : rawName;
            partitions[index] = new LpPartition(
                rawName,
                name,
                attributes,
                BinaryPrimitives.ReadUInt32LittleEndian(entry[40..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[44..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[48..]),
                0);
        }

        for (int index = 0; index < extents.Length; index++)
        {
            ReadEntry(set.Source, tablesOffset, header.Extents, index, entry[..LpFormat.ExtentEntrySize]);
            var targetType = (LpExtentTargetType)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            if (!Enum.IsDefined(targetType))
                throw Invalid(set, summary, "extent", index, Resources.Errors_Lp_ExtentTargetTypeUnknown);
            extents[index] = new LpExtent(
                BinaryPrimitives.ReadUInt64LittleEndian(entry),
                targetType,
                BinaryPrimitives.ReadUInt64LittleEndian(entry[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]));
        }

        for (int index = 0; index < groups.Length; index++)
        {
            ReadEntry(set.Source, tablesOffset, header.Groups, index, entry[..LpFormat.GroupEntrySize]);
            string rawName = ReadName(entry[..36]);
            var flags = (LpGroupFlags)BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]);
            if (((uint)flags & ~(uint)LpGroupFlags.SlotSuffixed) != 0)
                throw Invalid(set, summary, "group", index, Resources.Errors_Lp_GroupFlagsUnknown);
            groups[index] = new LpPartitionGroup(
                rawName,
                (flags & LpGroupFlags.SlotSuffixed) != 0 ? rawName + suffix : rawName,
                flags,
                BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]));
        }

        for (int index = 0; index < blockDevices.Length; index++)
        {
            ReadEntry(set.Source, tablesOffset, header.BlockDevices, index, entry);
            var flags = (LpBlockDeviceFlags)BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]);
            if (((uint)flags & ~(uint)LpBlockDeviceFlags.SlotSuffixed) != 0)
                throw Invalid(set, summary, "block_device", index, Resources.Errors_Lp_BlockDeviceFlagsUnknown);
            string rawName = ReadName(entry.Slice(24, 36));
            blockDevices[index] = new LpBlockDevice(
                BinaryPrimitives.ReadUInt64LittleEndian(entry),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]),
                rawName,
                (flags & LpBlockDeviceFlags.SlotSuffixed) != 0 ? rawName + suffix : rawName,
                flags);
        }

        ValidateAndSize(set, summary, partitions, extents, groups, blockDevices);
        return new LpMetadataDocument(
            set,
            summary,
            reservation,
            partitions,
            extents,
            groups,
            blockDevices);
    }

    public LpPartition GetPartition(string name)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (LpPartition partition in _partitions)
        {
            if (string.Equals(partition.Name, name, StringComparison.Ordinal)) return partition;
        }

        throw new KeyNotFoundException(Resources.FormatErrors_Lp_PartitionNameNotFound(name));
    }

    public LpLogicalPartitionBlockDevice OpenPartition(string name)
    {
        ThrowIfDisposed();
        if (_blockDevices.Length != 1)
        {
            throw new InvalidOperationException(
                Resources.Errors_Lp_MultipleBlockDevicesRequireResolver);
        }

        var resolver = new SingleSourceResolver(_set.Source);
        return OpenPartitionAsync(name, resolver).AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask<LpLogicalPartitionBlockDevice> OpenPartitionAsync(
        string name,
        ILpBlockDeviceResolver resolver,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(resolver);
        LpPartition partition = GetPartition(name);
        var leases = new IReadableBlockDeviceLease[_blockDevices.Length];
        int resolved = 0;
        try
        {
            while (resolved < leases.Length)
            {
                IReadableBlockDeviceLease lease = await resolver
                    .ResolveAsync(_blockDevices[resolved], cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidOperationException(Resources.ResolverReturnedNull);
                _ = lease.Device;
                leases[resolved] = lease;
                resolved++;
            }

            LpLogicalPartitionBlockDevice result = CreateMappedDevice(partition, leases);
            leases = null!;
            return result;
        }
        finally
        {
            if (leases is not null)
            {
                for (int index = 0; index < resolved; index++) leases[index]?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_lifetime.IsDisposed) return;
        _lifetime.Dispose();
        _reservation.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void ThrowIfDisposed()
    {
        _set.ThrowIfDisposed();
        _lifetime.ThrowIfDisposed(this);
    }

    private LpLogicalPartitionBlockDevice CreateMappedDevice(
        LpPartition partition,
        IReadableBlockDeviceLease[] leases)
    {
        var sources = new IReadableBlockDevice[leases.Length];
        for (int index = 0; index < sources.Length; index++) sources[index] = leases[index].Device;
        var mappedExtents = new BlockDeviceExtent[partition.ExtentCount];
        long logicalOffset = 0;
        for (int relative = 0; relative < mappedExtents.Length; relative++)
        {
            LpExtent extent = _extents[checked((int)partition.FirstExtentIndex + relative)];
            long length = checked((long)extent.SectorCount * LpFormat.SectorSize);
            mappedExtents[relative] = extent.TargetType == LpExtentTargetType.Zero
                ? new BlockDeviceExtent(logicalOffset, length, BlockExtentKind.Zero)
                : new BlockDeviceExtent(
                    logicalOffset,
                    length,
                    BlockExtentKind.Linear,
                    checked((long)extent.TargetData * LpFormat.SectorSize),
                    checked((int)extent.TargetSource));
            logicalOffset = checked(logicalOffset + length);
        }

        var mapped = new MappedBlockDevice(
            sources,
            new BlockDeviceId($"lp:{partition.Name}"),
            partition.LogicalSize,
            checked((int)_set.Geometry.LogicalBlockSize),
            mappedExtents,
            DeviceOwnership.Borrow);
        return new LpLogicalPartitionBlockDevice(this, mapped, leases, partition);
    }

    private static void ReadEntry(
        IReadableBlockDevice source,
        long tablesOffset,
        LpTableDescriptor descriptor,
        int index,
        Span<byte> destination)
    {
        long offset = checked(tablesOffset + descriptor.Offset + (long)index * descriptor.EntrySize);
        BlockDeviceIO.ReadExactlyAt(source, offset, destination);
    }

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        int terminator = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> name = terminator >= 0 ? bytes[..terminator] : bytes;
        return Encoding.UTF8.GetString(name);
    }

    private static void ValidateAndSize(
        LpMetadataSet set,
        LpMetadataCopySummary summary,
        LpPartition[] partitions,
        LpExtent[] extents,
        LpPartitionGroup[] groups,
        LpBlockDevice[] blockDevices)
    {
        if (groups.Length == 0 || blockDevices.Length == 0)
            throw Invalid(set, summary, "tables", 0, Resources.Errors_Lp_RequiredTablesEmpty);

        ulong reservedBytes = checked(
            (ulong)LpFormat.ReservedBytes +
            2UL * LpFormat.GeometryBlockSize +
            2UL * set.Geometry.MetadataMaxSize * set.Geometry.MetadataSlotCount);

        for (int index = 0; index < blockDevices.Length; index++)
        {
            LpBlockDevice device = blockDevices[index];
            if (device.Size > long.MaxValue || device.Alignment % LpFormat.SectorSize != 0 ||
                device.AlignmentOffset % LpFormat.SectorSize != 0 ||
                device.FirstLogicalSector > device.Size / LpFormat.SectorSize)
                throw Invalid(set, summary, "block_device", index, Resources.Errors_Lp_BlockDeviceGeometryInvalid);
            if (index == 0 && reservedBytes > device.FirstLogicalSector * LpFormat.SectorSize)
                throw Invalid(set, summary, "block_device", index, Resources.Errors_Lp_MetadataOverlapsAllocatableSectors);
        }

        for (int index = 0; index < extents.Length; index++)
        {
            LpExtent extent = extents[index];
            if (extent.SectorCount == 0)
                throw Invalid(set, summary, "extent", index, Resources.Errors_Lp_ExtentSectorCountZero);
            if (extent.TargetType == LpExtentTargetType.Zero)
            {
                if (extent.TargetData != 0 || extent.TargetSource != 0)
                    throw Invalid(set, summary, "extent", index, Resources.Errors_Lp_ZeroExtentReferencesStorage);
                continue;
            }

            if (extent.TargetSource >= blockDevices.Length)
                throw Invalid(set, summary, "extent", index, Resources.Errors_Lp_ExtentTargetSourceOutOfRange);
            LpBlockDevice device = blockDevices[extent.TargetSource];
            ulong sectors = device.Size / LpFormat.SectorSize;
            if (extent.TargetData < device.FirstLogicalSector ||
                extent.TargetData > sectors || extent.SectorCount > sectors - extent.TargetData)
                throw Invalid(set, summary, "extent", index, Resources.Errors_Lp_LinearExtentOutOfRange);
        }

        for (int index = 0; index < partitions.Length; index++)
        {
            LpPartition partition = partitions[index];
            ulong end = (ulong)partition.FirstExtentIndex + partition.ExtentCount;
            if (end > (ulong)extents.Length || partition.GroupIndex >= groups.Length)
                throw Invalid(set, summary, "partition", index, Resources.Errors_Lp_PartitionRangeInvalid);
            long logicalSize = 0;
            for (uint relative = 0; relative < partition.ExtentCount; relative++)
            {
                LpExtent extent = extents[partition.FirstExtentIndex + relative];
                logicalSize = checked(logicalSize + checked((long)extent.SectorCount * LpFormat.SectorSize));
            }
            partitions[index] = partition with { LogicalSize = logicalSize };
        }
    }

    private static ImageFormatException Invalid(
        LpMetadataSet set,
        LpMetadataCopySummary summary,
        string structure,
        int index,
        string message) =>
        LpMetadataSet.Failure(
            set.Source,
            ImageFormatErrorCode.CorruptMetadata,
            message,
            summary.Offset,
            structure,
            "ImageFormats.Lp.InvalidTableEntry",
            objectId: $"slot:{summary.SlotNumber}/{summary.Kind}/{structure}:{index}");

    private sealed class SingleSourceResolver(IReadableBlockDevice source) : ILpBlockDeviceResolver
    {
        public ValueTask<IReadableBlockDeviceLease> ResolveAsync(
            LpBlockDevice blockDevice,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((ulong)source.Length < blockDevice.Size)
                throw new ArgumentException(Resources.Errors_Lp_SourceSmallerThanBlockDevice, nameof(source));
            return ValueTask.FromResult<IReadableBlockDeviceLease>(
                new BlockDeviceLease(source, DeviceOwnership.Borrow));
        }
    }
}
