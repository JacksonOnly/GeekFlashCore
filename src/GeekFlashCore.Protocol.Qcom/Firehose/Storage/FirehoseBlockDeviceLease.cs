using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Storage;

public sealed class FirehoseBlockDeviceLease : IWritableBlockDeviceLease
{
    private FirehoseBlockDevice? _device;

    public FirehoseBlockDeviceLease(FirehoseBlockDevice device) =>
        _device = device ?? throw new ArgumentNullException(nameof(device));

    public IWritableBlockDevice Device =>
        Volatile.Read(ref _device) ?? throw new ObjectDisposedException(nameof(FirehoseBlockDeviceLease));

    IReadableBlockDevice IReadableBlockDeviceLease.Device => Device;

    public void Dispose() => Interlocked.Exchange(ref _device, null)?.Dispose();
}
