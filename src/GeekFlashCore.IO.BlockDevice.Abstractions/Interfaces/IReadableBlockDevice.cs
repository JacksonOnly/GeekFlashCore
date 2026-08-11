namespace GeekFlashCore.IO.BlockDevice.Abstractions;

public interface IReadableBlockDevice : IDisposable
{
    BlockDeviceId Id { get; }
    long Length { get; }
    int LogicalBlockSize { get; }

    int ReadAt(long offset, Span<byte> destination);
}
