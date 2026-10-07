using System.Buffers.Binary;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.Android.Sparse;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.ImageFormats.Abstractions;

namespace GeekFlashCore.Android.Lp;

/// <summary>A validated single-device LP layout that can map source images to virtual Sparse Super.</summary>
/// <remarks>Image factories are borrowed. No disk spool or expanded image buffer is created.</remarks>
public sealed class LpSuperImageLayout
{
    private readonly LpImageMetadataSource _metadata;
    private LpSuperImageLayout(LpGeometry geometry, LpMetadataDocument document, LpImageMetadataSource metadata, CancellationToken ct)
    {
        Geometry = geometry; Header = document.Header; Partitions = document.Partitions.ToArray(); Extents = document.Extents.ToArray();
        Groups = document.Groups.ToArray(); BlockDevices = document.BlockDevices.ToArray(); _metadata = metadata;
        if (BlockDevices.Length != 1 || BlockDevices.Span[0].Flags != LpBlockDeviceFlags.None ||
            Partitions.Span.ToArray().Any(p => (p.Attributes & LpPartitionAttributes.SlotSuffixed) != 0) ||
            Groups.Span.ToArray().Any(g => g.Flags != LpGroupFlags.None) || (Header.Flags & ~1U) != 0)
            throw new InvalidDataException(Resources.SuperImageUnsupported);
        Length = checked((long)BlockDevices.Span[0].Size);
        if (Length % geometry.LogicalBlockSize != 0 || Length / geometry.LogicalBlockSize > uint.MaxValue || metadata.Length > Length)
            throw new InvalidDataException(Resources.SuperImageInvalid);
        var ranges = Extents.Span.ToArray().OrderBy(e => e.TargetData).ToArray(); long previous = metadata.Length;
        foreach (var e in ranges)
        {
            ct.ThrowIfCancellationRequested();
            long start = checked((long)e.TargetData * 512), size = checked((long)e.SectorCount * 512);
            if (e.TargetType != LpExtentTargetType.Linear || e.TargetSource != 0 || start < previous || start % geometry.LogicalBlockSize != 0 || size % geometry.LogicalBlockSize != 0)
                throw new InvalidDataException(Resources.SuperImageUnsupported);
            previous = checked(start + size);
        }
        var groupSizes = new ulong[Groups.Length];
        foreach (var p in Partitions.Span) { ct.ThrowIfCancellationRequested(); groupSizes[p.GroupIndex] = checked(groupSizes[p.GroupIndex] + (ulong)p.LogicalSize); }
        for (int i = 0; i < Groups.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); ulong used = groupSizes[i];
            if (Groups.Span[i].MaximumSize != 0 && used > Groups.Span[i].MaximumSize) throw new InvalidDataException(Resources.FormatGroupCapacityExceeded(Groups.Span[i].RawName, Groups.Span[i].MaximumSize, used));
        }
    }
    /// <summary>Physical Super capacity in bytes.</summary>
    public long Length { get; }
    /// <summary>Validated metadata size, slot count and logical block size.</summary>
    public LpGeometry Geometry { get; }
    /// <summary>Metadata header, including Virtual A/B flags.</summary>
    public LpMetadataHeader Header { get; }
    /// <summary>Logical partitions, including zero-length inactive partitions.</summary>
    public ReadOnlyMemory<LpPartition> Partitions { get; }
    /// <summary>Physical extents in metadata order.</summary>
    public ReadOnlyMemory<LpExtent> Extents { get; }
    /// <summary>Partition group capacities and flags.</summary>
    public ReadOnlyMemory<LpPartitionGroup> Groups { get; }
    /// <summary>The single physical block device.</summary>
    public ReadOnlyMemory<LpBlockDevice> BlockDevices { get; }
    /// <summary>Size of the reserved geometry and all primary/backup metadata slots.</summary>
    public long MetadataLength => _metadata.Length;
    /// <summary>Opens an independent caller-owned stream over the bounded metadata prefix.</summary>
    public Stream OpenMetadataStream(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return _metadata.OpenStream(cancellationToken); }

    /// <summary>Reads a compact 4096-byte geometry plus metadata blob from a borrowed seekable stream.</summary>
    /// <remarks>The original stream position is restored. Checksums and models use the existing LP parser.</remarks>
    public static LpSuperImageLayout ReadMetadataBlob(Stream source, ImageReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); limits ??= ImageReadLimits.Default; limits.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException(Resources.SuperImageInvalid, nameof(source));
        long origin = source.Position;
        try
        {
            long length = checked(source.Length - origin);
            if (length < 4096 + 128 || length > 4096L + limits.MaxLpMetadataBytes) throw new InvalidDataException(Resources.SuperImageInvalid);
            byte[] geometryBlock = new byte[4096]; source.ReadExactly(geometryBlock); cancellationToken.ThrowIfCancellationRequested();
            var geometry = new LpGeometry(BinaryPrimitives.ReadUInt32LittleEndian(geometryBlock.AsSpan(40)),
                BinaryPrimitives.ReadUInt32LittleEndian(geometryBlock.AsSpan(44)), BinaryPrimitives.ReadUInt32LittleEndian(geometryBlock.AsSpan(48)));
            ValidateGeometry(geometry, limits);
            byte[] metadata = new byte[checked((int)length - 4096)]; source.ReadExactly(metadata); cancellationToken.ThrowIfCancellationRequested();
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(8)), tableSize = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(44));
            if ((ulong)headerSize + tableSize != (ulong)metadata.Length || metadata.Length > geometry.MetadataMaxSize)
                throw new InvalidDataException(Resources.SuperImageInvalid);
            return FromSource(geometry, new(geometry, geometryBlock, metadata), limits, cancellationToken);
        }
        finally { source.Position = origin; }
    }

    /// <summary>Creates a new layout using lpmake-style alignment, readonly defaults and bounded metadata.</summary>
    public static LpSuperImageLayout Create(LpSuperImageConfiguration configuration,
        IReadOnlyList<LpSuperPartitionDefinition> partitions, IReadOnlyList<LpSuperGroupDefinition>? groups = null,
        ImageReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(partitions); cancellationToken.ThrowIfCancellationRequested(); limits ??= ImageReadLimits.Default;
        limits.Validate(); var g = configuration.Geometry; ValidateGeometry(g, limits); LpNameValidator.Validate(configuration.DeviceName, nameof(configuration));
        if (configuration.DeviceSize <= 0 || configuration.DeviceSize % g.LogicalBlockSize != 0 ||
            configuration.Alignment > 64 * 1024 * 1024 || configuration.Alignment % g.LogicalBlockSize != 0 ||
            configuration.AlignmentOffset % g.LogicalBlockSize != 0 || (configuration.Alignment == 0 ? configuration.AlignmentOffset != 0 : configuration.AlignmentOffset >= configuration.Alignment))
            throw new ArgumentException(Resources.SuperImageInvalid, nameof(configuration));
        int count = partitions.Count, groupCount = groups?.Count ?? 0;
        if (count < 1 || count > limits.MaxLpTableEntries || groupCount > limits.MaxLpTableEntries || (count + (long)groupCount) * 256 > limits.MaxDecodedLpMetadataBytes)
            throw new InvalidDataException(Resources.MetadataTooLarge);
        var groupList = new List<LpPartitionGroup> { new("default", "default", LpGroupFlags.None, 0) };
        var groupIndices = new Dictionary<string, uint>(StringComparer.Ordinal) { ["default"] = 0 };
        bool declaredDefault = false;
        for (int i = 0; i < groupCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var item = groups![i]; ArgumentNullException.ThrowIfNull(item); LpNameValidator.Validate(item.Name, nameof(groups));
            if (item.Name == "default") { if (declaredDefault || item.MaximumSize != 0) throw new InvalidDataException(Resources.SuperImageInvalid); declaredDefault = true; continue; }
            if (!groupIndices.TryAdd(item.Name, (uint)groupList.Count)) throw new InvalidDataException(Resources.SuperImageInvalid);
            groupList.Add(new(item.Name, item.Name, LpGroupFlags.None, item.MaximumSize));
        }
        long firstByte = LpImageMetadataSource.RoundUp(checked(12288L + 2L * g.MetadataMaxSize * g.MetadataSlotCount), configuration.Alignment == 0 ? g.LogicalBlockSize : configuration.Alignment);
        if (firstByte >= configuration.DeviceSize) throw new InvalidDataException(Resources.InsufficientSpace);
        LpBlockDevice[] devices = [new((ulong)(firstByte / 512), configuration.Alignment, configuration.AlignmentOffset, (ulong)configuration.DeviceSize, configuration.DeviceName, configuration.DeviceName, LpBlockDeviceFlags.None)];
        var allocator = new LpSectorAllocator(devices, g.LogicalBlockSize, [new(0, (ulong)(firstByte / 512), (ulong)((configuration.DeviceSize - firstByte) / 512))]);
        var modelPartitions = new LpPartition[count]; var modelExtents = new List<LpExtent>(); var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var p = partitions[i]; ArgumentNullException.ThrowIfNull(p); LpNameValidator.Validate(p.Name, nameof(partitions));
            LpNameValidator.ValidatePartitionAttributes(p.Attributes, 2, nameof(partitions));
            if (p.Size < 0 || p.Size % g.LogicalBlockSize != 0 || !names.Add(p.Name) || !groupIndices.TryGetValue(p.GroupName, out uint group) || (p.Attributes & LpPartitionAttributes.SlotSuffixed) != 0)
                throw new InvalidDataException(Resources.SuperImageInvalid);
            if (!allocator.TryAllocate((ulong)(p.Size / 512), out var extents)) throw new InvalidDataException(Resources.InsufficientSpace);
            modelPartitions[i] = new(p.Name, p.Name, p.Attributes, (uint)modelExtents.Count, (uint)extents.Length, group, p.Size); modelExtents.AddRange(extents);
        }
        var header = new LpMetadataHeader(10, 2, 256, 0, default, default, default, default, configuration.VirtualAb ? 1U : 0U);
        byte[] encoded = LpMetadataEncoder.EncodeAndValidate(g, 0, new(header, modelPartitions, modelExtents.ToArray(), groupList.ToArray(), devices), limits);
        return FromSource(g, new(g, LpImageMetadataSource.EncodeGeometry(g), encoded), limits, cancellationToken);
    }

    /// <summary>Returns the compact metadata blob, allocating only the serialized metadata bytes.</summary>
    public byte[] ToMetadataBlob()
    {
        int size = checked((int)(Header.HeaderSize + Header.TablesSize)); byte[] result = new byte[checked(4096 + size)];
        _metadata.GeometryBlock.CopyTo(result, 0); _metadata.Metadata.AsSpan(0, size).CopyTo(result.AsSpan(4096)); return result;
    }

    /// <summary>Maps exact-length partition sources into this layout as virtual Sparse, with preserved empty space.</summary>
    public SparseImageComposition CreateSparseImage(IReadOnlyDictionary<string, Func<CancellationToken, Stream>> images,
        SparseImageCompositionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images); options ??= new() { MaximumSources = 128 }; cancellationToken.ThrowIfCancellationRequested();
        int required = 1; foreach (var p in Partitions.Span) if (p.LogicalSize != 0) required++;
        if (required > options.MaximumSources || Extents.Length + 1 > options.MaximumChunks || (required + (long)Extents.Length) * 512 > options.MaximumMetadataBytes)
            throw new InvalidDataException(Resources.MetadataTooLarge);
        var inputs = new List<SparseImageInput> { new(_metadata.OpenStream, false, _metadata.Length) };
        var placements = new List<SparseImagePlacement> { new(0, 0, 0, _metadata.Length) };
        foreach (var p in Partitions.Span)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (p.LogicalSize == 0) continue;
            if (!images.TryGetValue(p.RawName, out var factory) || factory is null) throw new InvalidDataException(Resources.SuperImageMissingImage);
            bool sparse; using (Stream probe = factory(cancellationToken)) { if (probe is null || !probe.CanRead || !probe.CanSeek) throw new InvalidDataException(Resources.SuperImageInvalid); probe.Position = 0; sparse = SparseImageParser.IsSparse(probe); }
            int index = inputs.Count; inputs.Add(new(factory, sparse, p.LogicalSize)); long offset = 0;
            for (int i = 0; i < p.ExtentCount; i++)
            {
                var e = Extents.Span[checked((int)p.FirstExtentIndex + i)]; long length = checked((long)e.SectorCount * 512);
                placements.Add(new(index, offset, checked((long)e.TargetData * 512), length)); offset = checked(offset + length);
            }
        }
        placements.Sort(static (a, b) => a.OutputOffset.CompareTo(b.OutputOffset)); cancellationToken.ThrowIfCancellationRequested();
        return SparseImageComposer.ComposeLayout(inputs, placements, Geometry.LogicalBlockSize, checked((uint)(Length / Geometry.LogicalBlockSize)), options, cancellationToken);
    }
    private static LpSuperImageLayout FromSource(LpGeometry geometry, LpImageMetadataSource source, ImageReadLimits limits, CancellationToken ct)
    {
        using var set = LpMetadataSet.Open(new CancelledMetadataSource(source, ct), DeviceOwnership.Borrow, limits); using var doc = set.OpenPreferredSlot(0);
        ct.ThrowIfCancellationRequested(); return new(geometry, doc, source, ct);
    }
    private sealed class CancelledMetadataSource(LpImageMetadataSource source, CancellationToken ct) : IReadableBlockDevice
    {
        public BlockDeviceId Id => source.Id;
        public long Length => source.Length;
        public int LogicalBlockSize => source.LogicalBlockSize;
        public int ReadAt(long offset, Span<byte> destination) { ct.ThrowIfCancellationRequested(); int n = source.ReadAt(offset, destination); ct.ThrowIfCancellationRequested(); return n; }
        public void Dispose() { }
    }
    private static void ValidateGeometry(LpGeometry g, ImageReadLimits limits)
    {
        if (g.MetadataMaxSize < 512 || g.MetadataMaxSize % 512 != 0 || g.MetadataMaxSize > limits.MaxLpMetadataBytes ||
            g.MetadataSlotCount < 1 || g.MetadataSlotCount > limits.MaxLpMetadataSlots || g.LogicalBlockSize is < 512 or > 1024 * 1024 ||
            (g.LogicalBlockSize & (g.LogicalBlockSize - 1)) != 0) throw new InvalidDataException(Resources.SuperImageInvalid);
    }
}
