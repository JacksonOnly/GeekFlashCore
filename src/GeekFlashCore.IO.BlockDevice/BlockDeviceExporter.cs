namespace GeekFlashCore.IO.BlockDevice;

public static class BlockDeviceExporter
{
    public static async ValueTask ExportAsync(
        IReadableBlockDevice source,
        Stream destination,
        IProgress<BlockCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        long length = source.Length;
        using var stream = new BlockDeviceStream(source, DeviceOwnership.Borrow);
        await BoundedStreamCopier.CopyExactlyAsync(
                stream,
                destination,
                length,
                BoundedStreamCopier.DefaultBufferSize,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async ValueTask ExportAsync(
        IReadableBlockDevice source,
        Stream destination,
        BudgetedArrayPool buffers,
        IProgress<BlockCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(buffers);
        long length = source.Length;
        using var stream = new BlockDeviceStream(source, DeviceOwnership.Borrow);
        await BoundedStreamCopier.CopyExactlyAsync(
                stream,
                destination,
                length,
                buffers,
                BoundedStreamCopier.DefaultBufferSize,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
