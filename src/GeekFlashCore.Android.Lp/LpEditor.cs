using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeekFlashCore.Android.Lp;

public sealed class LpEditor
{
    private readonly ILoggerFactory _loggerFactory;

    public LpEditor()
        : this(NullLoggerFactory.Instance)
    {
    }

    public LpEditor(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
    }

    public LpEditSession Open(
        IReadableBlockDevice source,
        DeviceOwnership ownership,
        int slotNumber,
        LpEditOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateOwnership(ownership);
        options ??= new LpEditOpenOptions();
        options.Validate();

        LpMetadataSet? metadata = null;
        try
        {
            metadata = LpMetadataSet.Open(source, ownership, options.ReadLimits);
            LpSessionSnapshot snapshot = LpSnapshotReader.Capture(metadata, slotNumber);
            return new LpEditSession(metadata, snapshot, slotNumber, options, _loggerFactory);
        }
        catch
        {
            metadata?.Dispose();
            throw;
        }
    }

    public async ValueTask<LpEditSession> OpenAsync(
        IReadableBlockDevice source,
        DeviceOwnership ownership,
        int slotNumber,
        ILpBlockDeviceResolver resolver,
        LpEditOpenOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolver);
        ValidateOwnership(ownership);
        options ??= new LpEditOpenOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        LpMetadataSet? metadata = null;
        try
        {
            metadata = LpMetadataSet.Open(source, ownership, options.ReadLimits);
            LpSessionSnapshot initial = LpSnapshotReader.Capture(
                metadata,
                slotNumber,
                cancellationToken: cancellationToken);
            var identities = new Dictionary<uint, LpDeviceIdentity>
            {
                [0] = initial.MetadataDevice
            };
            var ids = new HashSet<BlockDeviceId> { initial.MetadataDevice.Id };
            LpBlockDevice[] blockDevices = initial.Baseline.BlockDevices;
            for (int index = 1; index < blockDevices.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadableBlockDeviceLease lease = await resolver
                    .ResolveAsync(blockDevices[index], cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidOperationException(
                        Resources.ResolverReturnedNull);
                using (lease)
                {
                    IReadableBlockDevice device = lease.Device;
                    if (device.Length < 0 || (ulong)device.Length < blockDevices[index].Size)
                    {
                        throw new InvalidDataException(
                            Resources.DeviceIdentityMismatch);
                    }
                    if (!ids.Add(device.Id))
                    {
                        throw new InvalidDataException(Resources.AliasedBlockDevice);
                    }
                    identities[checked((uint)index)] = new LpDeviceIdentity(
                        device.Id,
                        device.Length,
                        device.LogicalBlockSize);
                }
            }

            var snapshot = new LpSessionSnapshot(
                initial.Geometry,
                initial.PrimaryGeometryDigest,
                initial.BackupGeometryDigest,
                initial.Slots,
                initial.Baseline,
                initial.BaselineKind,
                initial.MetadataDevice,
                identities);
            return new LpEditSession(metadata, snapshot, slotNumber, options, _loggerFactory);
        }
        catch
        {
            metadata?.Dispose();
            throw;
        }
    }

    private static void ValidateOwnership(DeviceOwnership ownership)
    {
        if (!Enum.IsDefined(ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }
    }
}
