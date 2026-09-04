using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Storage;

public sealed class FirehoseBlockDevice : IWritableBlockDevice
{
    private readonly BlockDeviceDescriptor _descriptor;
    private readonly bool _writable;
    private FirehoseStorageService? _service;

    public FirehoseBlockDevice(
        FirehoseStorageService service,
        BlockDeviceDescriptor descriptor,
        bool writable = true)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _writable = writable && descriptor.CanWrite;
        if (descriptor.PhysicalPartitionNumber is null)
            throw new ArgumentException("A Qualcomm block device requires a physical partition number.", nameof(descriptor));
    }

    public BlockDeviceId Id => _descriptor.Id;
    public long Length => _descriptor.Length;
    public int LogicalBlockSize => _descriptor.LogicalBlockSize;

    public int ReadAt(long offset, Span<byte> destination)
    {
        FirehoseStorageService service = GetService();
        if (destination.IsEmpty)
            return 0;
        FirehoseRangeValidator.ValidateByteRange(offset, destination.Length, LogicalBlockSize, Length);
        service.Read(new FirehoseReadRequest
        {
            PhysicalPartitionNumber = checked((uint)_descriptor.PhysicalPartitionNumber!.Value),
            StartSector = offset / LogicalBlockSize,
            SectorCount = destination.Length / LogicalBlockSize,
            SectorSizeInBytes = checked((uint)LogicalBlockSize)
        }, destination);
        return destination.Length;
    }

    public void WriteAt(long offset, ReadOnlySpan<byte> source)
    {
        FirehoseStorageService service = GetService();
        if (!_writable)
            throw new BlockDeviceException($"Qualcomm block device '{Id}' was opened read-only.");
        FirehoseRangeValidator.ValidateByteRange(offset, source.Length, LogicalBlockSize, Length);
        service.Program(
            checked((uint)_descriptor.PhysicalPartitionNumber!.Value),
            offset / LogicalBlockSize,
            source);
    }

    public void Flush() => _ = GetService();

    public void Dispose()
    {
        Interlocked.Exchange(ref _service, null);
        GC.SuppressFinalize(this);
    }

    private FirehoseStorageService GetService() =>
        Volatile.Read(ref _service) ?? throw new ObjectDisposedException(nameof(FirehoseBlockDevice));
}
