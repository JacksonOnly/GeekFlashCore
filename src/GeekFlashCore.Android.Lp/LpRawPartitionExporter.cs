using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;

namespace GeekFlashCore.Android.Lp;

/// <summary>Exports a logical partition as its fully expanded RAW byte sequence.</summary>
public static class LpRawPartitionExporter
{
    public static async ValueTask ExportAsync(
        LpMetadataDocument document,
        string partitionName,
        Stream destination,
        IProgress<BlockCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        ArgumentNullException.ThrowIfNull(destination);

        using LpLogicalPartitionBlockDevice partition = document.OpenPartition(partitionName);
        await BlockDeviceExporter.ExportAsync(
                partition,
                destination,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async ValueTask ExportAsync(
        LpMetadataDocument document,
        string partitionName,
        ILpBlockDeviceResolver resolver,
        Stream destination,
        IProgress<BlockCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(destination);

        using LpLogicalPartitionBlockDevice partition = await document
            .OpenPartitionAsync(partitionName, resolver, cancellationToken)
            .ConfigureAwait(false);
        await BlockDeviceExporter.ExportAsync(
                partition,
                destination,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
