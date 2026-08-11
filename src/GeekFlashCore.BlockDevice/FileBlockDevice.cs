using Microsoft.Win32.SafeHandles;

namespace GeekFlashCore.BlockDevice;

public sealed class FileBlockDevice : IReadableBlockDevice
{
    private readonly FileBlockDeviceCore _core;

    public FileBlockDevice(string path, int logicalBlockSize = 512)
    {
        _core = new FileBlockDeviceCore(
            path,
            logicalBlockSize,
            FileAccess.Read,
            FileShare.Read);
    }

    public FileBlockDevice(
        SafeFileHandle handle,
        BlockDeviceId id,
        int logicalBlockSize,
        DeviceOwnership ownership)
    {
        _core = new FileBlockDeviceCore(handle, id, logicalBlockSize, ownership);
    }

    public BlockDeviceId Id => _core.Id;
    public long Length => _core.Length;
    public int LogicalBlockSize => _core.LogicalBlockSize;

    public int ReadAt(long offset, Span<byte> destination)
    {
        return _core.ReadAt(offset, destination);
    }

    public void Dispose()
    {
        _core.Dispose();
        GC.SuppressFinalize(this);
    }
}
