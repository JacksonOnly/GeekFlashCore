namespace GeekFlashCore.IO.BlockDevice.Abstractions;

public interface IReadableBlockDeviceLease : IDisposable
{
    IReadableBlockDevice Device { get; }
}
