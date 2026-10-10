using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Sprd.Abstractions;

namespace GeekFlashCore.CLI;

/// <summary>Read-only named partition leases; no physical disk offsets or writable LP capabilities are invented.</summary>
internal sealed class SprdPartitionDeviceResolver(ISprdProtocol protocol, IReadOnlyList<PartitionInfo> partitions) : ILpBlockDeviceResolver
{
    internal IReadableBlockDevice Open(PartitionInfo partition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (partition.Name is not { } name || partition.Length is not > 0 || StorageCommands.PartitionLun(partition) != 0)
            throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
        var source = protocol.OpenPartition(name, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (source.Length != partition.Length) throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
            return source;
        }
        catch { source.Dispose(); throw; }
    }
    public ValueTask<IReadableBlockDeviceLease> ResolveAsync(LpBlockDevice blockDevice, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matches = partitions.Where(p => p.Name == blockDevice.PartitionName && StorageCommands.PartitionLun(p) == 0).Take(2).ToArray();
        if (matches.Length != 1) throw new ArgumentException(Strings.FormatCli_PartitionNotUnique(blockDevice.PartitionName));
        return ValueTask.FromResult<IReadableBlockDeviceLease>(new BlockDeviceLease(Open(matches[0], cancellationToken), DeviceOwnership.Transfer));
    }
}
