using System.Collections.ObjectModel;

namespace GeekFlashCore.IO.BlockDevice.Abstractions;

public sealed record BlockDeviceDescriptor
{
    public BlockDeviceDescriptor(
        BlockDeviceId id,
        long length,
        int logicalBlockSize,
        bool canWrite,
        string? storageType = null,
        int? physicalPartitionNumber = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (id.IsEmpty) throw new ArgumentException(Strings.BlockDeviceIdRequired, nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalBlockSize, 1);
        if (physicalPartitionNumber is < 0)
            throw new ArgumentOutOfRangeException(nameof(physicalPartitionNumber));

        Id = id;
        Length = length;
        LogicalBlockSize = logicalBlockSize;
        CanWrite = canWrite;
        StorageType = storageType;
        PhysicalPartitionNumber = physicalPartitionNumber;
        Metadata = metadata is null
            ? EmptyMetadata
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public BlockDeviceId Id { get; }
    public long Length { get; }
    public int LogicalBlockSize { get; }
    public bool CanWrite { get; }
    public string? StorageType { get; }
    public int? PhysicalPartitionNumber { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
