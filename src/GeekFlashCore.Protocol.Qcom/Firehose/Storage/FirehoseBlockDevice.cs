using System.Buffers;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Storage;

public sealed class FirehoseBlockDevice : IWritableBlockDevice
{
    private const int MaximumByteReadSectorSize = 64 * 1024;
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
            throw new ArgumentException(Strings.Qcom_BlockDevicePartitionRequired, nameof(descriptor));
    }

    public BlockDeviceId Id => _descriptor.Id;
    public long Length => _descriptor.Length;
    public int LogicalBlockSize => _descriptor.LogicalBlockSize;

    /// <summary>Reads bytes through aligned Firehose sectors, returning a short read at the device end.</summary>
    public int ReadAt(long offset, Span<byte> destination)
    {
        FirehoseStorageService service = GetService();
        int length = BlockDeviceIO.GetReadLength(this, offset, destination.Length);
        if (length == 0) return 0;
        destination = destination[..length];
        int sectorSize = LogicalBlockSize;
        int headOffset = (int)(offset % sectorSize);
        if (headOffset == 0 && length % sectorSize == 0)
        {
            ReadAligned(service, offset, destination);
            return length;
        }

        // Validate the last enclosing sector before any I/O, without rounding past Int64.MaxValue.
        long lastByte = checked(offset + length - 1);
        if (lastByte - lastByte % sectorSize > Length - sectorSize)
            throw new ArgumentOutOfRangeException(nameof(destination), Strings.Qcom_ByteRangeExceedsDevice);
        if (sectorSize > MaximumByteReadSectorSize)
            throw new BlockDeviceException(Strings.Qcom_ByteReadSectorTooLarge);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(sectorSize);
        try
        {
            Span<byte> sector = buffer.AsSpan(0, sectorSize);
            if (headOffset != 0)
            {
                ReadAligned(service, offset - headOffset, sector);
                int copied = Math.Min(destination.Length, sectorSize - headOffset);
                sector.Slice(headOffset, copied).CopyTo(destination);
                destination = destination[copied..];
                offset = checked(offset + copied);
            }
            int middleLength = destination.Length / sectorSize * sectorSize;
            if (middleLength > 0)
            {
                ReadAligned(service, offset, destination[..middleLength]);
                destination = destination[middleLength..];
                offset = checked(offset + middleLength);
            }
            if (!destination.IsEmpty)
            {
                ReadAligned(service, offset, sector);
                sector[..destination.Length].CopyTo(destination);
            }
            return length;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private void ReadAligned(FirehoseStorageService service, long offset, Span<byte> destination)
    {
        FirehoseRangeValidator.ValidateByteRange(offset, destination.Length, LogicalBlockSize, Length);
        service.Read(new FirehoseReadRequest
        {
            PhysicalPartitionNumber = checked((uint)_descriptor.PhysicalPartitionNumber!.Value),
            StartSector = offset / LogicalBlockSize,
            SectorCount = destination.Length / LogicalBlockSize,
            SectorSizeInBytes = checked((uint)LogicalBlockSize)
        }, destination);
    }

    public void WriteAt(long offset, ReadOnlySpan<byte> source)
    {
        FirehoseStorageService service = GetService();
        if (!_writable)
            throw new BlockDeviceException(Strings.FormatQcom_BlockDeviceOpenedReadOnly(Id));
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
