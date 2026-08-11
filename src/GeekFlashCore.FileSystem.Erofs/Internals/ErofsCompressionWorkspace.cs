using GeekFlashCore.IO.BlockDevice;

namespace GeekFlashCore.FileSystem.Erofs.Internals;

internal sealed class ErofsCompressionWorkspace : IDisposable
{
    private PooledBufferLease? _lease;

    public ErofsCompressionWorkspace(
        PooledBufferLease lease,
        bool reusableAcrossExtents)
    {
        _lease = lease;
        ReusableAcrossExtents = reusableAcrossExtents;
    }

    public Memory<byte> Memory => (_lease ??
                                   throw new ObjectDisposedException(nameof(ErofsCompressionWorkspace))).Memory;
    public bool ReusableAcrossExtents { get; }

    public PooledBufferLease DetachLease() => Interlocked.Exchange(ref _lease, null) ??
                                              throw new ObjectDisposedException(nameof(ErofsCompressionWorkspace));

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
