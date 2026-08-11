namespace GeekFlashCore.BlockDevice.Abstractions;

public interface IWritableBlockDeviceLease : IReadableBlockDeviceLease
{
    new IWritableBlockDevice Device { get; }
}
