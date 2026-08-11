namespace GeekFlashCore.BlockDevice.Abstractions;

public interface IWritableBlockDevice : IReadableBlockDevice
{
    void WriteAt(long offset, ReadOnlySpan<byte> source);
    void Flush();
}
