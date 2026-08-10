namespace GeekFlashCore.IO.BlockDevice;

public sealed class WritableBlockDeviceLease : IWritableBlockDeviceLease
{
    private IWritableBlockDevice? _device;
    private readonly bool _ownsDevice;

    public WritableBlockDeviceLease(IWritableBlockDevice device, DeviceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Enum.IsDefined(ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }

        _device = device;
        _ownsDevice = ownership == DeviceOwnership.Transfer;
    }

    public IWritableBlockDevice Device =>
        Volatile.Read(ref _device) ?? throw new ObjectDisposedException(nameof(WritableBlockDeviceLease));

    IReadableBlockDevice IReadableBlockDeviceLease.Device => Device;

    public void Dispose()
    {
        IWritableBlockDevice? device = Interlocked.Exchange(ref _device, null);
        if (_ownsDevice)
        {
            device?.Dispose();
        }
    }
}
