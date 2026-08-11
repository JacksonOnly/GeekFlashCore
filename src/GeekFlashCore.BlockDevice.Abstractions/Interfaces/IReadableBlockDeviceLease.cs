namespace GeekFlashCore.BlockDevice.Abstractions;

public interface IReadableBlockDeviceLease : IDisposable
{
    IReadableBlockDevice Device { get; }
}
