using Microsoft.Win32.SafeHandles;

namespace GeekFlashCore.BlockDevice;

public sealed class WritableFileBlockDevice :
    IWritableBlockDevice
{
    private readonly FileBlockDeviceCore _core;

    public WritableFileBlockDevice(string path, int logicalBlockSize = 512)
    {
        _core = new FileBlockDeviceCore(
            path,
            logicalBlockSize,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    public WritableFileBlockDevice(
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

    public void WriteAt(long offset, ReadOnlySpan<byte> source)
    {
        _core.WriteAt(offset, source);
    }

    public void Flush()
    {
        _core.Flush();
    }

    public void Dispose()
    {
        _core.Dispose();
        GC.SuppressFinalize(this);
    }
}
