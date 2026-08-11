using GeekFlashCore.Abstractions;

namespace GeekFlashCore.IO.BlockDevice.Abstractions;

public sealed class BlockDeviceException : GeekFlashCoreException
{
    public BlockDeviceException()
    {
    }

    public BlockDeviceException(string? message)
        : base(message)
    {
    }

    public BlockDeviceException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
