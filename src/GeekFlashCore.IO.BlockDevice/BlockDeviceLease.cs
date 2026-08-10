namespace GeekFlashCore.IO.BlockDevice;

public sealed class BlockDeviceLease : IReadableBlockDeviceLease
{
    private IReadableBlockDevice? _device;
    private readonly bool _ownsDevice;

    public BlockDeviceLease(IReadableBlockDevice device, DeviceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Enum.IsDefined(ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }

        _device = device;
        _ownsDevice = ownership == DeviceOwnership.Transfer;
    }

    public IReadableBlockDevice Device =>
        Volatile.Read(ref _device) ?? throw new ObjectDisposedException(nameof(BlockDeviceLease));

    public void Dispose()
    {
        IReadableBlockDevice? device = Interlocked.Exchange(ref _device, null);
        if (_ownsDevice)
        {
            device?.Dispose();
        }
    }
}
