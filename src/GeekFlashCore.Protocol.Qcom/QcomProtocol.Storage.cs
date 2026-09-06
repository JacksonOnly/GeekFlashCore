using System.Buffers.Binary;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Gpt;
using GeekFlashCore.Gpt.Abstractions;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Shared.Utilities;
using Serilog;

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
            StartSector = range.Start, SectorCount = range.Count, SectorSizeInBytes = range.SectorSize, Label = range.Label,
            PadToSectorCount = source.Target is not PartitionTarget };
        long written = _storage!.Program(request, ProgramProgress(progress), ct);
        return Task.FromResult(written);
    }

    public Task<ReadDestination> ReadAsync(ReadDestination destination, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var operation = EnterConnected();
        ct.ThrowIfCancellationRequested();
        var range = ResolveTarget(destination.Target, ct);
        var request = new FirehoseReadRequest { PhysicalPartitionNumber = range.Partition,
            StartSector = range.Start, SectorCount = range.Count, SectorSizeInBytes = range.SectorSize, Label = range.Label };
        ReadWithProgress(request, destination.OutputStream, progress, ct);
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
        return Task.FromResult(ToPartitionInfos(ReadPartitions(ct)));
    }

    public Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(uint physicalPartitionNumber,
        IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        using var operation = EnterConnected();
        ValidateLun(physicalPartitionNumber);
        return Task.FromResult(ToPartitionInfos(ReadPartitions(ct, physicalPartitionNumber)));
    }

    private static IReadOnlyList<PartitionInfo> ToPartitionInfos(List<(string Name, TargetRange Range)> entries) =>
        entries.Select(item => new PartitionInfo(
            item.Name, checked(item.Range.Start * item.Range.SectorSize), item.Range.Start,
            checked(item.Range.Count * item.Range.SectorSize), new Dictionary<string, string>
            { ["PhysicalPartitionNumber"] = item.Range.Partition.ToString(System.Globalization.CultureInfo.InvariantCulture) })).ToArray();

    public IReadOnlyList<uint> GetPhysicalPartitions()
    {
        using var operation = EnterConnected();
        return KnownLuns();
    }

    private uint[] KnownLuns()
    {
        var infos = _targetInfo!.Firehose!.StorageInfos;
        if (infos.FirstOrDefault(x => x.PhysicalPartitionNumber == 0)?.Properties.TryGetValue("num_physical", out string? value) == true)
        {
            if (!uint.TryParse(value, out uint count) || count is 0 or > FirehoseConstants.MaximumPhysicalPartitionCount)
                throw new QcomProtocolException(Strings.Qcom_InvalidLunCount);
            return Enumerable.Range(0, checked((int)count)).Select(x => (uint)x).ToArray();
        }
        return infos.Select(x => x.PhysicalPartitionNumber).Distinct().Order().ToArray();
    }

    private void ValidateLun(uint lun)
    {
        if (lun >= FirehoseConstants.MaximumPhysicalPartitionCount || !KnownLuns().Contains(lun))
            throw new ArgumentOutOfRangeException(nameof(lun), Strings.Qcom_UnknownLun);
    }

    public FirehoseStorageInfo GetStorageInfo(uint physicalPartitionNumber)
    {
        using var operation = EnterConnected();
        ValidateLun(physicalPartitionNumber);
        return QueryStorageInfo(physicalPartitionNumber);
    }

    private FirehoseStorageInfo QueryStorageInfo(uint lun)
    {
        var info = _storage!.GetStorageInfo(lun);
        // Some loaders only report the LUN count during the first storage query.
        var previous = _targetInfo!.Firehose!.StorageInfos.FirstOrDefault(x => x.PhysicalPartitionNumber == lun);
        if (!info.Properties.ContainsKey("num_physical") && previous?.Properties.TryGetValue("num_physical", out string? count) == true)
        {
            var properties = new Dictionary<string, string>(info.Properties, StringComparer.OrdinalIgnoreCase)
                { ["num_physical"] = count };
            info = info with { Properties = properties };
        }
        _targetInfo = _targetInfo! with { Firehose = _targetInfo.Firehose! with
        {
            StorageInfos = _targetInfo.Firehose.StorageInfos.Where(x => x.PhysicalPartitionNumber != lun)
                .Append(info).OrderBy(x => x.PhysicalPartitionNumber).ToArray()
        } };
        return info;
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
                var matches = ReadPartitions(ct, target.PhysicalPartitionNumber).Where(x => x.Name == named.Name &&
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

    private List<(string Name, TargetRange Range)> ReadPartitions(CancellationToken ct, uint? selectedLun = null)
    {
        var partitions = new List<(string, TargetRange)>();
        if (selectedLun is { } selected) ValidateLun(selected);
        foreach (uint lun in selectedLun is { } value ? [value] : KnownLuns())
        {
            ct.ThrowIfCancellationRequested();
            if (!_targetInfo!.Firehose!.StorageInfos.Any(x => x.PhysicalPartitionNumber == lun))
                QueryStorageInfo(lun);
            var descriptor = _storage!.GetBlockDevices().FirstOrDefault(x => x.PhysicalPartitionNumber == lun);
            if (descriptor is null)
            {
                Log.ForContext<QcomProtocol>().Warning(Strings.Qcom_LogGptSkipped, lun, Strings.Qcom_StorageCapacityMissing);
                continue;
            }
            int sectorSize = descriptor.LogicalBlockSize;
            try
            {
                if (descriptor.Length < checked(sectorSize * 2L))
                    throw new GptException(Strings.Qcom_GptInvalidGeometry);
                byte[] header = new byte[checked(sectorSize * 2)];
                ReadGptMetadata(lun, 0, sectorSize, header, ct);
                // HeaderOnly in the image parser still requires the declared entry array.
                // Validate untrusted header geometry before allocating and reading that array.
                int imageLength = GetPrimaryGptImageLength(header.AsSpan(sectorSize), sectorSize, descriptor.Length);
                byte[] image = new byte[imageLength];
                header.CopyTo(image, 0);
                ReadGptMetadata(lun, 2, sectorSize, image.AsSpan(header.Length), ct);
                var table = new GptParser().Parse(image, new GptParseOptions
                {
                    SectorSize = sectorSize, CrcPolicy = GptCrcPolicy.Strict,
                    AllowUnpatchedPartitionGeometry = false, AllowEmptyPartitionTypeId = false,
                    SkipEmptyPartitionTypeId = true
                });
                foreach (var entry in table.Entries)
                    partitions.Add((entry.Name, new(lun,
                        checked((long)entry.FirstLba), checked((long)entry.SectorCount), checked((uint)sectorSize), entry.Name)));
            }
            catch (GptException exception)
            {
                Log.ForContext<QcomProtocol>().Warning(Strings.Qcom_LogGptSkipped, lun, exception.Message);
            }
        }
        return partitions;
    }

    private void ReadGptMetadata(uint lun, long start, int sectorSize, Span<byte> buffer, CancellationToken ct) =>
        _storage!.Read(new FirehoseReadRequest
        {
            PhysicalPartitionNumber = lun, StartSector = start,
            SectorCount = buffer.Length / sectorSize, SectorSizeInBytes = checked((uint)sectorSize)
        }, buffer, cancellationToken: ct);

    private static int GetPrimaryGptImageLength(ReadOnlySpan<byte> header, int sectorSize, long deviceLength)
    {
        const ulong maximumMetadataLength = 16 * 1024 * 1024;
        if (!header[..8].SequenceEqual("EFI PART"u8))
            throw new GptException(Strings.Qcom_GptSignatureMissing);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        ulong current = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
        ulong backup = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
        ulong last = BinaryPrimitives.ReadUInt64LittleEndian(header[48..]);
        ulong entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        ulong diskSectors = checked((ulong)(deviceLength / sectorSize));
        if (headerSize < 92 || headerSize > sectorSize || current != 1 || backup <= current ||
            backup >= diskSectors || first > last || last >= backup ||
            count is 0 or > 16384 || entrySize < 128 || (entrySize & 7) != 0 ||
            entriesLba < 2 || entriesLba > maximumMetadataLength / (ulong)sectorSize)
            throw new GptException(Strings.Qcom_GptInvalidGeometry);
        ulong end = checked(entriesLba * (ulong)sectorSize + (ulong)count * entrySize);
        ulong sectors = checked((end + (ulong)sectorSize - 1) / (ulong)sectorSize);
        ulong aligned = checked(sectors * (ulong)sectorSize);
        if (aligned > maximumMetadataLength || aligned > (ulong)deviceLength || sectors > first)
            throw new GptException(Strings.Qcom_GptInvalidGeometry);
        byte[] crcHeader = header[..checked((int)headerSize)].ToArray();
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcHeader.AsSpan(16));
        crcHeader.AsSpan(16, 4).Clear();
        if (Crc32Helper.Compute(crcHeader) != expectedCrc)
            throw new GptException(Strings.Qcom_GptHeaderCrcInvalid);
        return checked((int)aligned);
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
