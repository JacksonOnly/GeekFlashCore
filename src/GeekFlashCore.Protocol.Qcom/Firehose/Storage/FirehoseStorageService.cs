using System.Globalization;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Storage;

public sealed class FirehoseStorageService : IBlockDeviceProvider
{
    private readonly Dictionary<BlockDeviceId, BlockDeviceDescriptor> _descriptors = [];
    private readonly FirehoseConfigureResponse _configuration;
    private readonly FirehoseSession _session;

    public FirehoseStorageService(FirehoseSession session, FirehoseConfigureResponse configuration)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
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
        _ = GetTransferBufferSize();
    }

    public FirehoseConfigureResponse Configuration => _configuration;

    public long Read(
        FirehoseReadRequest request,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The Firehose read destination is not writable.", nameof(destination));
        request.Validate();
        FirehoseRangeValidator.ValidateSectorRange(
            request.PhysicalPartitionNumber,
            request.StartSector,
            request.SectorCount,
            request.SectorSizeInBytes);
        long length = request.GetByteLength();
        _session.Execute(CreateReadCommand(request), expectedRawMode: true);
        return _session.ReceiveRaw(destination, length, GetTransferBufferSize(), progress, cancellationToken)
            .BytesTransferred;
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
            throw new ArgumentException("The destination length must equal the Firehose read range.", nameof(destination));
        _session.Execute(CreateReadCommand(request), expectedRawMode: true);
        return _session.ReceiveRaw(destination, progress, cancellationToken).BytesTransferred;
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
        if (request.Format == FirehoseProgramFormat.AndroidSparse)
            throw new NotSupportedException("Sparse images must be executed through the sparse program planner.");

        long sourceLength = request.GetSourceLength();
        if (sourceLength == 0)
            throw new ArgumentException("The Firehose program source cannot be empty.", nameof(request));
        long wireLength = request.GetWireLength();
        using Stream source = request.Source.OpenStream() ??
                              throw new InvalidDataException("The Firehose program source returned no stream.");
        if (!source.CanRead)
            throw new InvalidDataException("The Firehose program source is not readable.");
        PositionSource(source, request.SourceOffset, cancellationToken);

        _session.Execute(CreateProgramCommand(request), expectedRawMode: true);
        return _session.SendRaw(
            source,
            sourceLength,
            wireLength,
            GetTransferBufferSize(),
            request.PaddingByte,
            progress,
            cancellationToken).BytesTransferred;
    }

    public long Program(
        uint physicalPartitionNumber,
        long startSector,
        ReadOnlySpan<byte> source,
        CancellationToken cancellationToken = default)
    {
        uint sectorSize = _configuration.SectorSizeInBytes;
        if (source.IsEmpty || source.Length % sectorSize != 0)
            throw new ArgumentException("The Firehose program buffer must contain aligned sectors.", nameof(source));
        long sectors = source.Length / sectorSize;
        FirehoseRangeValidator.ValidateSectorRange(
            physicalPartitionNumber,
            startSector,
            sectors,
            sectorSize);
        _session.Execute(new ProgramCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            SectorSizeInBytes = sectorSize,
            StartSector = Format(startSector),
            NumPartitionSectors = Format(sectors)
        }, expectedRawMode: true);
        return _session.SendRaw(source, GetTransferBufferSize(), cancellationToken).BytesTransferred;
    }

    public FirehoseCommandResult Erase(
        uint physicalPartitionNumber,
        long startSector,
        long sectorCount,
        FirehoseIoOptions? options = null)
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
        });
    }

    public FirehoseCommandResult SetBootableStorageDrive(uint value) =>
        value < FirehoseConstants.MaximumPhysicalPartitionCount
            ? _session.Execute(new SetBootableStorageDriveCommand { Value = value })
            : throw new ArgumentOutOfRangeException(nameof(value));

    public FirehoseCommandResult Patch(
        uint physicalPartitionNumber,
        long startSector,
        uint byteOffset,
        uint sizeInBytes,
        string value,
        string fileName = "DISK")
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
        return _session.Execute(new PatchCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber,
            SectorSizeInBytes = _configuration.SectorSizeInBytes,
            StartSector = Format(startSector),
            ByteOffset = byteOffset,
            SizeInBytes = sizeInBytes,
            Value = value,
            FileName = fileName
        });
    }

    public FirehoseCommandResult Power(FirehosePowerValue value, ulong? delayInSeconds = null) =>
        Enum.IsDefined(value)
            ? _session.Execute(new PowerCommand { Value = value, DelayInSeconds = delayInSeconds })
            : throw new ArgumentOutOfRangeException(nameof(value));

    public long Peek(
        ulong address,
        Span<byte> destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty)
            throw new ArgumentException("The Firehose peek destination cannot be empty.", nameof(destination));
        _session.Execute(new PeekCommand
        {
            Address64 = address.ToString(CultureInfo.InvariantCulture),
            SizeInBytes = checked((ulong)destination.Length)
        }, expectedRawMode: true);
        return _session.ReceiveRaw(destination, progress, cancellationToken).BytesTransferred;
    }

    public byte[] GetSha256Digest(
        uint physicalPartitionNumber,
        long startSector,
        long sectorCount,
        FirehoseIoOptions? options = null)
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
        });
        if (FirehoseStorageInfoParser.TryFindSha256(result, out byte[] digest))
            return digest;
        throw new InvalidDataException("Firehose acknowledged getsha256digest without a SHA-256 value.");
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
                throw new BlockDeviceException($"Unknown Qualcomm block device '{id}'.");
        }
        options ??= new BlockDeviceOpenOptions();
        options.Limits.Validate();
        if (options.Writable && !descriptor.CanWrite)
            throw new BlockDeviceException($"Qualcomm block device '{id}' is read-only.");
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

    private int GetTransferBufferSize()
    {
        ulong value = _configuration.MaxPayloadSizeToTargetInBytesSupported > 0
            ? _configuration.MaxPayloadSizeToTargetInBytesSupported
            : _configuration.MaxPayloadSizeToTargetInBytes;
        if (value is 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(_configuration));
        return checked((int)value);
    }

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

    private static ProgramCommand CreateProgramCommand(FirehoseProgramRequest request) => new()
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

    private static void PositionSource(Stream source, long offset, CancellationToken cancellationToken)
    {
        if (offset == 0)
            return;
        if (source.CanSeek)
        {
            if (source.Seek(offset, SeekOrigin.Begin) != offset)
                throw new EndOfStreamException("Unable to seek to the Firehose source offset.");
            return;
        }

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long remaining = offset;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                    throw new EndOfStreamException("The Firehose source ended before its requested offset.");
                remaining -= read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte? ToByte(bool? value) => value is null ? null : value.Value ? (byte)1 : (byte)0;
    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
