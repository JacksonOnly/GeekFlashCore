using System.Buffers.Binary;
using System.Globalization;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose.Programming;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Storage;

public sealed class FirehoseStorageService : IBlockDeviceProvider
{
    private readonly Dictionary<BlockDeviceId, BlockDeviceDescriptor> _descriptors = [];
    private readonly FirehoseConfigureResponse _configuration;
    private readonly FirehoseProgramExecutor _programExecutor;
    private readonly FirehoseSession _session;
    private readonly IFirehoseStoragePolicy? _policy;
    private Func<(string PublicKey, string Token)>? _onePlusTokenFactory;

    public FirehoseStorageService(FirehoseSession session, FirehoseConfigureResponse configuration,
        IFirehoseStoragePolicy? policy = null)
    {
        _policy = policy;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (policy is Vendors.Oplus.OplusDigestLegacyPolicy legacy) legacy.Attach(session);
        ArgumentNullException.ThrowIfNull(configuration);
        FirehoseStorage storage = configuration.Storage != FirehoseStorage.None
            ? configuration.Storage
            : FirehoseStorageInfoParser.ParseStorage(configuration.MemoryName) ?? throw new ArgumentException(
                "The negotiated Firehose storage type is unknown.",
                nameof(configuration));
        uint sectorSize = configuration.SectorSizeInBytes != 0
            ? configuration.SectorSizeInBytes
            : storage is FirehoseStorage.Emmc or FirehoseStorage.Nvme
                ? 512u
                : 4096u;
        _configuration = configuration with
        {
            Storage = storage,
            MemoryName = storage.ToWireString(),
            SectorSizeInBytes = sectorSize
        };
        _programExecutor = new FirehoseProgramExecutor(_session, GetTransferBufferSize(), policy,
            () => _onePlusTokenFactory?.Invoke());
    }

    public FirehoseConfigureResponse Configuration => _configuration;

    internal void SetOnePlusTokenFactory(Func<(string PublicKey, string Token)> factory) =>
        _onePlusTokenFactory = factory ?? throw new ArgumentNullException(nameof(factory));

    public long Read(
        FirehoseReadRequest request,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException(Strings.Qcom_FirehoseReadDestinationNotWritable, nameof(destination));
        request.Validate();
        FirehoseRangeValidator.ValidateSectorRange(
            request.PhysicalPartitionNumber,
            request.StartSector,
            request.SectorCount,
            request.SectorSizeInBytes);
        long length = request.GetByteLength();
        IReadOnlyList<FirehoseStorageRange> ranges = Map(request.PhysicalPartitionNumber, request.StartSector,
            request.SectorCount, false, request.Label, request.FileName);
        long completed = 0;
        foreach (FirehoseStorageRange range in ranges)
        {
            ExecuteTransfer(CreateReadCommand(request with { StartSector = range.StartSector,
                SectorCount = range.SectorCount, Label = range.Label, FileName = range.FileName }), cancellationToken);
            long count = checked(range.SectorCount * request.SectorSizeInBytes);
            completed = checked(completed + _session.ReceiveRaw(destination, count, GetTransferBufferSize(),
                Aggregate(progress, completed), cancellationToken).BytesTransferred);
            _policy?.CommandCompleted();
        }
        return completed;
    }

    public long Read(
        FirehoseReadRequest request,
        Span<byte> destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        FirehoseRangeValidator.ValidateSectorRange(
            request.PhysicalPartitionNumber,
            request.StartSector,
            request.SectorCount,
            request.SectorSizeInBytes);
        long length = request.GetByteLength();
        if (length != destination.Length)
            throw new ArgumentException(Strings.Qcom_FirehoseDestinationLengthMismatch, nameof(destination));
        IReadOnlyList<FirehoseStorageRange> ranges = Map(request.PhysicalPartitionNumber, request.StartSector,
            request.SectorCount, false, request.Label, request.FileName);
        int completed = 0;
        foreach (FirehoseStorageRange range in ranges)
        {
            ExecuteTransfer(CreateReadCommand(request with { StartSector = range.StartSector,
                SectorCount = range.SectorCount, Label = range.Label, FileName = range.FileName }), cancellationToken);
            int count = checked((int)(range.SectorCount * request.SectorSizeInBytes));
            _session.ReceiveRaw(destination.Slice(completed, count), Aggregate(progress, completed), cancellationToken);
            completed = checked(completed + count);
            _policy?.CommandCompleted();
        }
        return completed;
    }

    public long Program(
        FirehoseProgramRequest request,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        FirehoseRangeValidator.ValidateSectorRange(
            request.PhysicalPartitionNumber,
            request.StartSector,
            request.SectorCount,
            request.SectorSizeInBytes);
        using Stream source = request.Source.OpenStream() ??
            throw new InvalidDataException(Strings.Qcom_FirehoseProgramStreamMissing);
        return _programExecutor.Execute(
            request,
            source,
            progress,
            cancellationToken);
    }

    public long Program(
        uint physicalPartitionNumber,
        long startSector,
        ReadOnlySpan<byte> source,
        CancellationToken cancellationToken = default)
    {
        uint sectorSize = _configuration.SectorSizeInBytes;
        if (source.IsEmpty || source.Length % sectorSize != 0)
            throw new ArgumentException(Strings.Qcom_FirehoseProgramBufferUnaligned, nameof(source));
        long sectors = source.Length / sectorSize;
        FirehoseRangeValidator.ValidateSectorRange(
            physicalPartitionNumber,
            startSector,
            sectors,
            sectorSize);
        IReadOnlyList<FirehoseStorageRange> ranges = Map(physicalPartitionNumber, startSector, sectors, true);
        int completed = 0;
        foreach (FirehoseStorageRange range in ranges)
        {
            ProgramCommand command = new()
            {
                PhysicalPartitionNumber = physicalPartitionNumber,
                SectorSizeInBytes = sectorSize,
                StartSector = Format(range.StartSector),
                NumPartitionSectors = Format(range.SectorCount),
                Label = range.Label,
                FileName = range.FileName
            };
            ApplyOnePlusCredential(command);
            ExecuteTransfer(command, cancellationToken);
            int count = checked((int)(range.SectorCount * sectorSize));
            _session.SendRaw(source.Slice(completed, count), GetTransferBufferSize(), cancellationToken);
            completed = checked(completed + count);
            _policy?.CommandCompleted();
        }
        return completed;
    }

    public FirehoseCommandResult Erase(
        uint physicalPartitionNumber,
        long startSector,
        long sectorCount,
        FirehoseIoOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        uint sectorSize = _configuration.SectorSizeInBytes;
        FirehoseRangeValidator.ValidateSectorRange(
            physicalPartitionNumber,
            startSector,
            sectorCount,
            sectorSize);
        return _session.Execute(new EraseCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            SectorSizeInBytes = sectorSize,
            StartSector = Format(startSector),
            NumPartitionSectors = Format(sectorCount),
            LastSector = options?.LastSector,
            SkipBadBlock = ToByte(options?.SkipBadBlock),
            GetSpare = ToByte(options?.GetSpare),
            EccDisabled = ToByte(options?.EccDisabled)
        }, cancellationToken: cancellationToken);
    }

    public FirehoseCommandResult SetBootableStorageDrive(uint value,
        CancellationToken cancellationToken = default) =>
        value < FirehoseConstants.MaximumPhysicalPartitionCount
            ? _session.Execute(new SetBootableStorageDriveCommand { Value = value }, cancellationToken: cancellationToken)
            : throw new ArgumentOutOfRangeException(nameof(value));

    public FirehoseCommandResult FixGpt(
        uint physicalPartitionNumber,
        string lun = "all",
        bool growLastPartition = true,
        CancellationToken cancellationToken = default)
    {
        if (physicalPartitionNumber >= FirehoseConstants.MaximumPhysicalPartitionCount)
            throw new ArgumentOutOfRangeException(nameof(physicalPartitionNumber));
        ArgumentException.ThrowIfNullOrWhiteSpace(lun);
        return _session.Execute(new FixGptCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            Lun = lun,
            GrowLastPartition = growLastPartition ? (byte)1 : (byte)0
        }, cancellationToken: cancellationToken);
    }

    public FirehoseCommandResult XblGpt(uint lun, CancellationToken cancellationToken = default)
    {
        if (lun >= FirehoseConstants.MaximumPhysicalPartitionCount)
            throw new ArgumentOutOfRangeException(nameof(lun));
        return _session.Execute(new XblGptCommand { Lun = lun }, cancellationToken: cancellationToken);
    }

    public FirehoseCommandResult Benchmark(
        uint physicalPartitionNumber,
        uint trials = 1,
        bool testDigestPerformance = true,
        bool testWritePerformance = true,
        bool testReadPerformance = true,
        CancellationToken cancellationToken = default)
    {
        if (physicalPartitionNumber >= FirehoseConstants.MaximumPhysicalPartitionCount)
            throw new ArgumentOutOfRangeException(nameof(physicalPartitionNumber));
        if (trials == 0)
            throw new ArgumentOutOfRangeException(nameof(trials));
        return _session.Execute(new BenchmarkCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            Trials = trials,
            TestDigestPerformance = testDigestPerformance ? 1u : 0u,
            TestWritePerformance = testWritePerformance ? 1u : 0u,
            TestReadPerformance = testReadPerformance ? 1u : 0u
        }, cancellationToken: cancellationToken);
    }

    public FirehoseCommandResult FirmwareWrite(
        uint physicalPartitionNumber,
        ReadOnlySpan<byte> firmware,
        CancellationToken cancellationToken = default)
    {
        if (physicalPartitionNumber >= FirehoseConstants.MaximumPhysicalPartitionCount)
            throw new ArgumentOutOfRangeException(nameof(physicalPartitionNumber));
        if (firmware.IsEmpty)
            throw new ArgumentException(Strings.Qcom_FirmwarePayloadEmpty, nameof(firmware));
        _session.Execute(new FirmwareWriteCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            SectorSizeInBytes = 1,
            NumPartitionSectors = checked((ulong)firmware.Length)
        }, expectedRawMode: true, cancellationToken: cancellationToken);
        return _session.SendRaw(firmware, GetTransferBufferSize(), cancellationToken);
    }

    public FirehoseCommandResult Patch(
        uint physicalPartitionNumber,
        long startSector,
        uint byteOffset,
        uint sizeInBytes,
        string value,
        string fileName = "DISK",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        FirehoseRangeValidator.ValidateSectorRange(
            physicalPartitionNumber,
            startSector,
            1,
            _configuration.SectorSizeInBytes);
        if (sizeInBytes == 0 || checked((ulong)byteOffset + sizeInBytes) > _configuration.SectorSizeInBytes)
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes));
        PatchCommand command = new()
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            SectorSizeInBytes = _configuration.SectorSizeInBytes,
            StartSector = Format(startSector),
            ByteOffset = byteOffset,
            SizeInBytes = sizeInBytes,
            Value = value,
            FileName = fileName
        };
        ApplyOnePlusCredential(command);
        return _session.Execute(command, cancellationToken: cancellationToken);
    }

    public FirehoseCommandResult Power(FirehosePowerValue value, ulong? delayInSeconds = null,
        CancellationToken cancellationToken = default) =>
        Enum.IsDefined(value)
            ? _session.Execute(new PowerCommand { Value = value, DelayInSeconds = delayInSeconds }, cancellationToken: cancellationToken)
            : throw new ArgumentOutOfRangeException(nameof(value));

    public long Peek(
        ulong address,
        Span<byte> destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty)
            throw new ArgumentException(Strings.Qcom_FirehosePeekDestinationEmpty, nameof(destination));
        _session.Execute(new PeekCommand
        {
            Address64 = address.ToString(CultureInfo.InvariantCulture),
            SizeInBytes = checked((ulong)destination.Length)
        }, expectedRawMode: true, cancellationToken: cancellationToken);
        return _session.ReceiveRaw(destination, progress, cancellationToken).BytesTransferred;
    }

    public byte[] GetSha256Digest(
        uint physicalPartitionNumber,
        long startSector,
        long sectorCount,
        FirehoseIoOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        uint sectorSize = _configuration.SectorSizeInBytes;
        FirehoseRangeValidator.ValidateSectorRange(
            physicalPartitionNumber,
            startSector,
            sectorCount,
            sectorSize);
        FirehoseCommandResult result = _session.Execute(new GetSha256DigestCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            SectorSizeInBytes = sectorSize,
            StartSector = Format(startSector),
            NumPartitionSectors = Format(sectorCount),
            LastSector = options?.LastSector,
            SkipBadBlock = ToByte(options?.SkipBadBlock),
            GetSpare = ToByte(options?.GetSpare),
            EccDisabled = ToByte(options?.EccDisabled)
        }, cancellationToken: cancellationToken);
        if (FirehoseStorageInfoParser.TryFindSha256(result, out byte[] digest))
            return digest;
        throw new InvalidDataException(Strings.Qcom_FirehoseDigestMissing);
    }

    public FirehoseCommandResult Nop(CancellationToken cancellationToken = default) =>
        _session.Execute(new NopCommand(), cancellationToken: cancellationToken);

    public long Peek(ulong address, long length, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException(Strings.Firehose_DestinationStreamNotWritable, nameof(destination));
        ValidateMemoryRange(address, length);
        Span<byte> buffer = stackalloc byte[256];
        long completed = 0;
        while (completed < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, length - completed);
            FirehoseCommandResult result = _session.Execute(new PeekCommand
            {
                Address64 = checked(address + (ulong)completed).ToString(CultureInfo.InvariantCulture),
                SizeInBytes = (ulong)count
            }, expectedRawMode: false, cancellationToken: cancellationToken);
            if (DecodePeekLogs(result, buffer[..count]) != count)
                throw new QcomProtocolException(Strings.Qcom_PeekLengthMismatch);
            destination.Write(buffer[..count]);
            buffer.Clear();
            completed += count;
        }
        return completed;
    }

    public long Poke(ulong address, IDataSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        long length = source.Length;
        ValidateMemoryRange(address, length);
        cancellationToken.ThrowIfCancellationRequested();
        using Stream stream = source.OpenStream() ??
            throw new InvalidDataException(Strings.Qcom_FirehoseProgramStreamMissing);
        if (!stream.CanRead)
            throw new ArgumentException(Strings.Firehose_SourceStreamNotReadable, nameof(source));
        Span<byte> buffer = stackalloc byte[8];
        for (long completed = 0; completed < length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, length - completed);
            buffer.Clear();
            stream.ReadExactly(buffer[..count]);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            _session.Execute(new PokeCommand
            {
                Address64 = checked(address + (ulong)completed), SizeInBytes = (uint)count,
                Value64 = "0x" + value.ToString("X16", CultureInfo.InvariantCulture)
            }, cancellationToken: cancellationToken);
            completed += count;
        }
        buffer.Clear();
        return length;
    }

    public FirehoseCommandResult FirmwareWrite(uint physicalPartitionNumber, IDataSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        long length = source.Length;
        if (length is <= 0 or > FirehoseConstants.MaximumRawTransferLength)
            throw new ArgumentOutOfRangeException(nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        using Stream stream = source.OpenStream() ??
            throw new InvalidDataException(Strings.Qcom_FirehoseProgramStreamMissing);
        if (!stream.CanRead)
            throw new ArgumentException(Strings.Firehose_SourceStreamNotReadable, nameof(source));
        _session.Execute(new FirmwareWriteCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            SectorSizeInBytes = 1,
            NumPartitionSectors = checked((ulong)length)
        }, expectedRawMode: true, cancellationToken: cancellationToken);
        return _session.SendRaw(stream, length, length,
            Math.Min(GetTransferBufferSize(), 1024 * 1024), cancellationToken: cancellationToken);
    }

    public FirehoseStorageInfo GetStorageInfo(uint physicalPartitionNumber)
    {
        if (physicalPartitionNumber >= FirehoseConstants.MaximumPhysicalPartitionCount)
            throw new ArgumentOutOfRangeException(nameof(physicalPartitionNumber));
        FirehoseCommandResult result = _session.Execute(new GetStorageInfoCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            PrintJson = 1
        });
        FirehoseStorageInfo info = FirehoseStorageInfoParser.ParseStorageInfo(result, physicalPartitionNumber);
        if (info.Storage == FirehoseStorage.None || info.BlockSizeInBytes is null)
        {
            info = info with
            {
                Storage = info.Storage == FirehoseStorage.None ? _configuration.Storage : info.Storage,
                BlockSizeInBytes = info.BlockSizeInBytes ?? _configuration.SectorSizeInBytes
            };
        }
        Register(info);
        return info;
    }

    public FirehoseBasicDevInfo GetBasicDeviceInfo()
    {
        FirehoseCommandResult result = _session.Execute(new NopCommand());
        return FirehoseStorageInfoParser.ParseBasicInfo(result);
    }

    public IReadOnlyList<BlockDeviceDescriptor> GetBlockDevices()
    {
        lock (_descriptors)
            return [.. _descriptors.Values];
    }

    public IReadableBlockDevice OpenBlockDevice(BlockDeviceId id, BlockDeviceOpenOptions? options = null)
    {
        BlockDeviceDescriptor descriptor;
        lock (_descriptors)
        {
            if (!_descriptors.TryGetValue(id, out descriptor!))
            throw new BlockDeviceException(Strings.FormatQcom_BlockDeviceUnknown(id));
        }
        options ??= new BlockDeviceOpenOptions();
        options.Limits.Validate();
        if (options.Writable && !descriptor.CanWrite)
            throw new BlockDeviceException(Strings.FormatQcom_BlockDeviceReadOnly(id));
        return new FirehoseBlockDevice(this, descriptor, options.Writable);
    }

    private void Register(FirehoseStorageInfo info)
    {
        if (info.BlockSizeInBytes is not { } blockSize || blockSize > int.MaxValue ||
            info.CapacityInBytes is not { } capacity || capacity > long.MaxValue)
            return;
        var id = new BlockDeviceId($"qcom:{info.Storage.ToWireString()}:{info.PhysicalPartitionNumber}");
        lock (_descriptors)
        {
            _descriptors[id] = new BlockDeviceDescriptor(
                id,
                checked((long)capacity),
                checked((int)blockSize),
                canWrite: true,
                info.Storage.ToWireString(),
                checked((int)info.PhysicalPartitionNumber),
                info.Properties);
        }
    }

    private int GetTransferBufferSize() => FirehosePayloadLimits.GetTransferBufferSize(_configuration);

    private static ReadCommand CreateReadCommand(FirehoseReadRequest request) => new()
    {
        Storage = request.Storage,
        Slot = request.Slot,
        PhysicalPartitionNumber = request.PhysicalPartitionNumber,
        SectorSizeInBytes = request.SectorSizeInBytes,
        NumPartitionSectors = Format(request.SectorCount),
        StartSector = Format(request.StartSector),
        Label = request.Label,
        FileName = request.FileName,
        LastSector = request.IoOptions.LastSector,
        SkipBadBlock = ToByte(request.IoOptions.SkipBadBlock),
        GetSpare = ToByte(request.IoOptions.GetSpare),
        EccDisabled = ToByte(request.IoOptions.EccDisabled)
    };

    private void ApplyOnePlusCredential(ProgramCommand command)
    {
        if (_onePlusTokenFactory?.Invoke() is not { } credential)
            return;
        command.PublicKey = credential.PublicKey;
        command.Token = credential.Token;
    }

    private void ApplyOnePlusCredential(PatchCommand command)
    {
        if (_onePlusTokenFactory?.Invoke() is not { } credential)
            return;
        command.PublicKey = credential.PublicKey;
        command.Token = credential.Token;
    }

    private IReadOnlyList<FirehoseStorageRange> Map(uint partition, long start, long count,
        bool write, string? label = null, string? fileName = null)
    {
        IReadOnlyList<FirehoseStorageRange>? ranges = _policy is null
            ? [new FirehoseStorageRange(start, count, label, fileName)]
            : _policy.Map(partition, start, count, write, label, fileName);
        return FirehoseStorageRangeValidator.Validate(ranges, start, count);
    }

    private void ExecuteTransfer(BaseCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_policy is null) _session.Execute(command, expectedRawMode: true, cancellationToken: cancellationToken);
        else _policy.ExecuteCommand(_session, command, cancellationToken);
    }

    private static IProgress<long>? Aggregate(IProgress<long>? progress, long origin) =>
        progress is null ? null : new AggregateProgress(progress, origin);

    private sealed class AggregateProgress(IProgress<long> target, long origin) : IProgress<long>
    {
        public void Report(long value) => target.Report(checked(origin + value));
    }

    private static byte? ToByte(bool? value) => value is null ? null : value.Value ? (byte)1 : (byte)0;
    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void ValidateMemoryRange(ulong address, long length)
    {
        if (length <= 0 || (ulong)length > ulong.MaxValue - address)
            throw new ArgumentOutOfRangeException(nameof(length));
    }

    private static int DecodePeekLogs(FirehoseCommandResult result, Span<byte> destination)
    {
        int decoded = 0;
        foreach (FirehoseResponseLog log in result.Logs)
        {
            string[] tokens = log.Message.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !tokens.All(IsHexBytes))
                continue;
            foreach (string token in tokens)
            {
                ReadOnlySpan<char> hex = token.AsSpan();
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hex = hex[2..];
                if (hex.Length / 2 > destination.Length - decoded)
                    throw new QcomProtocolException(Strings.Qcom_PeekLengthMismatch);
                for (int i = 0; i < hex.Length; i += 2)
                    destination[decoded++] = byte.Parse(hex.Slice(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }
        return decoded;
    }

    private static bool IsHexBytes(string token)
    {
        ReadOnlySpan<char> hex = token.AsSpan();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];
        return !hex.IsEmpty && hex.Length % 2 == 0 && !hex.ContainsAnyExcept("0123456789abcdefABCDEF");
    }

}
