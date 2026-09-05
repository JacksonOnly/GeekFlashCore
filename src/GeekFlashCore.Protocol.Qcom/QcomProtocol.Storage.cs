using System.Buffers.Binary;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Gpt;
using GeekFlashCore.Gpt.Abstractions;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom;

public sealed partial class QcomProtocol
{
    public IReadOnlyList<BlockDeviceDescriptor> GetBlockDevices()
    {
        using var operation = EnterConnected();
        return _storage!.GetBlockDevices();
    }

    public IReadableBlockDevice OpenBlockDevice(BlockDeviceId id, BlockDeviceOpenOptions? options = null)
    {
        using var operation = EnterConnected();
        return new SessionBlockDevice(this, _storage!.OpenBlockDevice(id, options), _generation);
    }

    public IReadableBlockDeviceLease OpenBlockDeviceLease(BlockDeviceId id, BlockDeviceOpenOptions? options = null) =>
        new SessionBlockDeviceLease(OpenBlockDevice(id, options));

    public Task<long> WriteAsync(WriteSource source, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var operation = EnterConnected();
        ct.ThrowIfCancellationRequested();
        var range = ResolveTarget(source.Target, ct);
        var request = new FirehoseProgramRequest { Source = source.Source, PhysicalPartitionNumber = range.Partition,
            StartSector = range.Start, SectorCount = range.Count, SectorSizeInBytes = range.SectorSize, Label = range.Label };
        return Task.FromResult(_storage!.Program(request, Adapt(progress, request.GetWireLength()), ct));
    }

    public Task<ReadDestination> ReadAsync(ReadDestination destination, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var operation = EnterConnected();
        ct.ThrowIfCancellationRequested();
        var range = ResolveTarget(destination.Target, ct);
        var request = new FirehoseReadRequest { PhysicalPartitionNumber = range.Partition,
            StartSector = range.Start, SectorCount = range.Count, SectorSizeInBytes = range.SectorSize, Label = range.Label };
        _storage!.Read(request, destination.OutputStream, Adapt(progress, request.GetByteLength()), ct);
        return Task.FromResult(destination);
    }

    public Task<bool> EraseAsync(StorageTarget target, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        using var operation = EnterConnected();
        ct.ThrowIfCancellationRequested();
        var range = ResolveTarget(target, ct);
        _storage!.Erase(range.Partition, range.Start, range.Count);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        using var operation = EnterConnected();
        return Task.FromResult<IReadOnlyList<PartitionInfo>>(ReadPartitions(ct).Select(item => new PartitionInfo(
            item.Name, checked(item.Range.Start * item.Range.SectorSize), item.Range.Start,
            checked(item.Range.Count * item.Range.SectorSize), new Dictionary<string, string>
            { ["PhysicalPartitionNumber"] = item.Range.Partition.ToString(System.Globalization.CultureInfo.InvariantCulture) })).ToArray());
    }

    private TargetRange ResolveTarget(StorageTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        uint size = _storage!.Configuration.SectorSizeInBytes;
        uint partition = target.PhysicalPartitionNumber ?? 0;
        TargetRange range;
        switch (target)
        {
            case SectorTarget sectors:
                if (sectors.SectorSize != size) throw new ArgumentException(Strings.Qcom_TargetSectorSizeMismatch, nameof(target));
                range = new(partition, sectors.StartSector, sectors.SectorCount, size);
                break;
            case OffsetTarget bytes:
                if (bytes.StartOffset % size != 0 || bytes.Length % size != 0)
                    throw new ArgumentException(Strings.Qcom_OffsetTargetNotAligned, nameof(target));
                range = new(partition, bytes.StartOffset / size, bytes.Length / size, size);
                break;
            case PartitionTarget named:
                var matches = ReadPartitions(ct).Where(x => x.Name == named.Name &&
                    (target.PhysicalPartitionNumber is null || x.Range.Partition == partition)).ToArray();
                if (matches.Length == 0) throw new ArgumentException(string.Format(Strings.Qcom_PartitionNotFound, named.Name), nameof(target));
                if (matches.Length > 1) throw new ArgumentException(string.Format(Strings.Qcom_PartitionAmbiguousAcrossLuns, named.Name), nameof(target));
                range = matches[0].Range;
                break;
            default: throw new ArgumentException(string.Format(Strings.Qcom_UnsupportedStorageTarget, target.GetType().Name), nameof(target));
        }
        Firehose.Storage.FirehoseRangeValidator.ValidateSectorRange(range.Partition, range.Start, range.Count, range.SectorSize);
        return range;
    }

    private List<(string Name, TargetRange Range)> ReadPartitions(CancellationToken ct)
    {
        var partitions = new List<(string, TargetRange)>();
        foreach (var descriptor in _storage!.GetBlockDevices())
        {
            ct.ThrowIfCancellationRequested();
            using var device = _storage.OpenBlockDevice(descriptor.Id);
            int sectorSize = descriptor.LogicalBlockSize;
            byte[] header = new byte[checked(sectorSize * 2)];
            device.ReadAt(0, header);
            if (!header.AsSpan(sectorSize, 8).SequenceEqual("EFI PART"u8)) continue;
            var parsedHeader = new GptParser().Parse(header, new GptParseOptions { SectorSize = sectorSize, HeaderOnly = true });
            ulong entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(sectorSize + 72));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(sectorSize + 80));
            uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(sectorSize + 84));
            ulong end = checked(entriesLba * (ulong)sectorSize + (ulong)count * entrySize);
            ulong aligned = checked((end + (ulong)sectorSize - 1) / (ulong)sectorSize * (ulong)sectorSize);
            if (aligned > 16 * 1024 * 1024 || aligned > (ulong)device.Length || aligned < (ulong)header.Length)
            throw new GptException(Strings.Qcom_GptEntryArrayExceedsRange);
            byte[] image = new byte[checked((int)aligned)];
            header.CopyTo(image, 0);
            ct.ThrowIfCancellationRequested();
            if (image.Length > header.Length) device.ReadAt(header.Length, image.AsSpan(header.Length));
            var table = new GptParser().Parse(image, new GptParseOptions { SectorSize = sectorSize });
            foreach (var entry in table.Entries)
                partitions.Add((entry.Name, new(checked((uint)descriptor.PhysicalPartitionNumber!.Value),
                    checked((long)entry.FirstLba), checked((long)entry.SectorCount), checked((uint)sectorSize), entry.Name)));
        }
        return partitions;
    }

    private readonly record struct TargetRange(uint Partition, long Start, long Count, uint SectorSize, string? Label = null);

    private sealed class SessionBlockDevice : IWritableBlockDevice
    {
        private readonly QcomProtocol _owner;
        private readonly long _generation;
        private IReadableBlockDevice? _device;

        public SessionBlockDevice(QcomProtocol owner, IReadableBlockDevice device, long generation)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _generation = generation;
        }

        public BlockDeviceId Id => Current.Id;
        public long Length => Current.Length;
        public int LogicalBlockSize => Current.LogicalBlockSize;

        private IReadableBlockDevice Current =>
            Volatile.Read(ref _device) ?? throw new ObjectDisposedException(nameof(SessionBlockDevice));

        private Operation EnterDevice()
        {
            _ = Current;
            var operation = _owner.EnterConnected();
            if (_generation != _owner._generation)
            {
                operation.Dispose();
                throw new QcomSessionInvalidException(Strings.Qcom_InvalidSessionState);
            }
            return operation;
        }

        public int ReadAt(long offset, Span<byte> destination)
        { using var operation = EnterDevice(); return Current.ReadAt(offset, destination); }
        public void WriteAt(long offset, ReadOnlySpan<byte> source)
        { using var operation = EnterDevice(); ((IWritableBlockDevice)Current).WriteAt(offset, source); }
        public void Flush()
        { using var operation = EnterDevice(); ((IWritableBlockDevice)Current).Flush(); }
        public void Dispose() => Interlocked.Exchange(ref _device, null)?.Dispose();
    }

    private sealed class SessionBlockDeviceLease(IReadableBlockDevice device) : IReadableBlockDeviceLease
    {
        private IReadableBlockDevice? _device = device;
        public IReadableBlockDevice Device => _device ?? throw new ObjectDisposedException(nameof(SessionBlockDeviceLease));
        public void Dispose() => Interlocked.Exchange(ref _device, null)?.Dispose();
    }
}
