using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.BlockDevice;

/// <summary>Exposes a logical device assembled from linear and zero extents.</summary>
public sealed class MappedBlockDevice : IReadableBlockDevice
{
    private readonly IReadableBlockDevice[] _sources;
    private readonly BlockDeviceExtent[] _extents;
    private readonly bool _ownsSources;
    private bool _disposed;

    public MappedBlockDevice(
        IReadOnlyList<IReadableBlockDevice> sources,
        BlockDeviceId id,
        long length,
        int logicalBlockSize,
        IEnumerable<BlockDeviceExtent> extents,
        DeviceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(extents);
        if (sources.Count == 0)
            throw new ArgumentException(Strings.AtLeastOneSourceRequired, nameof(sources));
        if (id.IsEmpty)
            throw new ArgumentException(Strings.BlockDeviceIdRequired, nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalBlockSize, 1);
        if (!Enum.IsDefined(ownership))
            throw new ArgumentOutOfRangeException(nameof(ownership));

        _sources = new IReadableBlockDevice[sources.Count];
        for (int index = 0; index < sources.Count; index++)
        {
            _sources[index] = sources[index] ??
                throw new ArgumentException(Strings.SourceDevicesContainNull, nameof(sources));
        }

        _extents = extents.ToArray();
        _ownsSources = ownership == DeviceOwnership.Transfer;
        Id = id;
        Length = length;
        LogicalBlockSize = logicalBlockSize;
        ValidateExtents();
    }

    public BlockDeviceId Id { get; }
    public long Length { get; }
    public int LogicalBlockSize { get; }

    public int ReadAt(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        int totalLength = BlockDeviceIO.GetReadLength(this, offset, destination.Length);
        int written = 0;
        while (written < totalLength)
        {
            long logicalOffset = checked(offset + written);
            BlockDeviceExtent extent = FindExtent(logicalOffset);
            int partLength = (int)Math.Min(totalLength - written, ExtentEnd(extent) - logicalOffset);
            Span<byte> part = destination.Slice(written, partLength);
            if (extent.Kind == BlockExtentKind.Zero)
            {
                part.Clear();
            }
            else
            {
                IReadableBlockDevice source = _sources[extent.SourceIndex];
                long physicalOffset = checked(extent.PhysicalOffset + logicalOffset - extent.LogicalOffset);
                BlockDeviceIO.ReadExactlyAt(source, physicalOffset, part);
            }
            written += partLength;
        }
        return totalLength;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsSources)
        {
            var disposed = new HashSet<IReadableBlockDevice>(ReferenceEqualityComparer.Instance);
            foreach (IReadableBlockDevice source in _sources)
            {
                if (disposed.Add(source))
                    source.Dispose();
            }
        }
        GC.SuppressFinalize(this);
    }

    private void ValidateExtents()
    {
        long expectedOffset = 0;
        foreach (BlockDeviceExtent extent in _extents)
        {
            if (extent.LogicalOffset != expectedOffset || extent.Length <= 0 ||
                extent.PhysicalOffset < 0 || extent.SourceIndex < 0 || !Enum.IsDefined(extent.Kind))
                throw new ArgumentException(Strings.ExtentsMustBeSortedAndCoverDevice, nameof(_extents));

            long logicalEnd = checked(extent.LogicalOffset + extent.Length);
            if (logicalEnd > Length)
                throw new ArgumentException(Strings.ExtentExceedsLogicalDevice, nameof(_extents));

            if (extent.Kind == BlockExtentKind.Zero)
            {
                if (extent.PhysicalOffset != 0 || extent.SourceIndex != 0)
                    throw new ArgumentException(Strings.ZeroExtentReferencesStorage, nameof(_extents));
            }
            else
            {
                if ((uint)extent.SourceIndex >= (uint)_sources.Length)
                    throw new ArgumentException(Strings.LinearExtentUnknownSource, nameof(_extents));
                IReadableBlockDevice source = _sources[extent.SourceIndex];
                if (extent.PhysicalOffset > source.Length - extent.Length)
                    throw new ArgumentException(Strings.LinearExtentExceedsSource, nameof(_extents));
            }
            expectedOffset = logicalEnd;
        }

        if (expectedOffset != Length)
            throw new ArgumentException(Strings.ExtentsMustCoverDevice, nameof(_extents));
    }

    private BlockDeviceExtent FindExtent(long logicalOffset)
    {
        int low = 0;
        int high = _extents.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            BlockDeviceExtent extent = _extents[middle];
            if (logicalOffset < extent.LogicalOffset)
                high = middle - 1;
            else if (logicalOffset >= ExtentEnd(extent))
                low = middle + 1;
            else
                return extent;
        }
        throw new InvalidDataException(Strings.LogicalOffsetNotCovered);
    }

    private static long ExtentEnd(BlockDeviceExtent extent) => checked(extent.LogicalOffset + extent.Length);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
