using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Sparse.BlockDevice;

internal sealed class SeekableStreamBlockDevice(Stream source, long origin) : IReadableBlockDevice
{
    private bool _disposed;
    public BlockDeviceId Id { get; } = new("stream:sparse-writer");
    public long Length { get; } = checked(source.Length - origin);
    public int LogicalBlockSize => 1;

    public int ReadAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (destination.IsEmpty || offset >= Length)
            return 0;

        int length = (int)Math.Min(destination.Length, Length - offset);
        source.Position = checked(origin + offset);
        return source.Read(destination[..length]);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
