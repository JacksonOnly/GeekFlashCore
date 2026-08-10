using Microsoft.Win32.SafeHandles;

namespace GeekFlashCore.IO.BlockDevice;

public sealed class WritableFileBlockDevice :
    IWritableBlockDevice
{
    private readonly SafeFileHandle _handle;
    private readonly bool _ownsHandle;
    private int _disposed;

    public WritableFileBlockDevice(string path, int logicalBlockSize = 512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalBlockSize, 1);

        string fullPath = Path.GetFullPath(path);
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.RandomAccess);
            long length = RandomAccess.GetLength(handle);

            _handle = handle;
            _ownsHandle = true;
            Id = new BlockDeviceId($"file:{fullPath}");
            Length = length;
            LogicalBlockSize = logicalBlockSize;
        }
        catch
        {
            handle?.Dispose();
            throw;
        }
    }

    public WritableFileBlockDevice(
        SafeFileHandle handle,
        BlockDeviceId id,
        int logicalBlockSize,
        DeviceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException(Strings.FileHandleInvalid, nameof(handle));
        }

        if (id.IsEmpty)
        {
            throw new ArgumentException(Strings.BlockDeviceIdRequired, nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(logicalBlockSize, 1);
        if (!Enum.IsDefined(ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }

        _handle = handle;
        _ownsHandle = ownership == DeviceOwnership.Transfer;
        Id = id;
        Length = RandomAccess.GetLength(handle);
        LogicalBlockSize = logicalBlockSize;
    }
    public BlockDeviceId Id { get; }
    public long Length { get; }
    public int LogicalBlockSize { get; }

    public int ReadAt(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        int length = BlockDeviceIO.GetReadLength(this, offset, destination.Length);
        if (length == 0)
        {
            return 0;
        }

        int read = RandomAccess.Read(_handle, destination[..length], offset);
        return BlockDeviceIO.ValidateReadResult(read, length);
    }

    public void WriteAt(long offset, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > Length - source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (!source.IsEmpty)
        {
            RandomAccess.Write(_handle, source, offset);
        }
    }

    public void Flush()
    {
        ThrowIfDisposed();
        RandomAccess.FlushToDisk(_handle);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsHandle)
        {
            _handle.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
