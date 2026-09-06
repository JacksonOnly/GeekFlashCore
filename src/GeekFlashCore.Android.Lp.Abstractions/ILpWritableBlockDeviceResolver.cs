using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp.Abstractions;

public interface ILpWritableBlockDeviceResolver
{
    ValueTask<IWritableBlockDeviceLease> ResolveAsync(
        LpBlockDevice blockDevice,
        CancellationToken cancellationToken = default);
}
