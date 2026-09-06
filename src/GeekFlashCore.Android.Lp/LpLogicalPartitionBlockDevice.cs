using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

public sealed class LpLogicalPartitionBlockDevice : IReadableBlockDevice
{
    private readonly LpMetadataDocument _document;
    private readonly MappedBlockDevice _mapped;
    private IReadableBlockDeviceLease[]? _leases;

    internal LpLogicalPartitionBlockDevice(
        LpMetadataDocument document,
        MappedBlockDevice mapped,
        IReadableBlockDeviceLease[] leases,
        LpPartition partition)
    {
        _document = document;
        _mapped = mapped;
        _leases = leases;
        Partition = partition;
    }

    public LpPartition Partition { get; }
    public BlockDeviceId Id => _mapped.Id;
    public long Length => _mapped.Length;
    public int LogicalBlockSize => _mapped.LogicalBlockSize;

    public int ReadAt(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        _document.ThrowIfDisposed();
        return _mapped.ReadAt(offset, destination);
    }

    public void Dispose()
    {
        IReadableBlockDeviceLease[]? leases = Interlocked.Exchange(ref _leases, null);
        if (leases is null) return;
        _mapped.Dispose();
        var disposed = new HashSet<IReadableBlockDeviceLease>(ReferenceEqualityComparer.Instance);
        foreach (IReadableBlockDeviceLease lease in leases)
        {
            if (disposed.Add(lease)) lease.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_leases is null, this);
}
