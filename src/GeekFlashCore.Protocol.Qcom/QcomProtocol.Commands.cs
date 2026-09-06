using System.Globalization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom;

public sealed partial class QcomProtocol
{
    public FirehoseCommandResult Nop(CancellationToken cancellationToken = default)
    {
        using var operation = EnterConnected();
        cancellationToken.ThrowIfCancellationRequested();
        return _storage!.Nop(cancellationToken);
    }

    public FirehoseCommandResult SetBootableStorageDrive(uint physicalPartitionNumber,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterConnected();
        ValidateLun(physicalPartitionNumber);
        cancellationToken.ThrowIfCancellationRequested();
        return _storage!.SetBootableStorageDrive(physicalPartitionNumber, cancellationToken);
    }

    public FirehoseCommandResult XblGpt(uint physicalPartitionNumber,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterConnected();
        ValidateLun(physicalPartitionNumber);
        cancellationToken.ThrowIfCancellationRequested();
        return _storage!.XblGpt(physicalPartitionNumber, cancellationToken);
    }

    public FirehoseCommandResult FixGpt(uint physicalPartitionNumber, bool growLastPartition = true,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterConnected();
        ValidateLun(physicalPartitionNumber);
        cancellationToken.ThrowIfCancellationRequested();
        return _storage!.FixGpt(physicalPartitionNumber,
            physicalPartitionNumber.ToString(CultureInfo.InvariantCulture), growLastPartition, cancellationToken);
    }

    public FirehoseCommandResult Patch(uint physicalPartitionNumber, long startSector, uint byteOffset,
        uint sizeInBytes, ulong value, CancellationToken cancellationToken = default)
    {
        using var operation = EnterConnected();
        ValidateLun(physicalPartitionNumber);
        ValidateCachedSectorRange(physicalPartitionNumber, startSector, 1);
        uint sectorSize = _storage!.Configuration.SectorSizeInBytes;
        if (sizeInBytes is not (1 or 2 or 4 or 8) || checked((ulong)byteOffset + sizeInBytes) > sectorSize)
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes));
        if (sizeInBytes < sizeof(ulong) && value >= (1UL << checked((int)sizeInBytes * 8)))
            throw new ArgumentOutOfRangeException(nameof(value));
        cancellationToken.ThrowIfCancellationRequested();
        return _storage.Patch(physicalPartitionNumber, startSector, byteOffset, sizeInBytes,
            value.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
    }

    public FirehoseCommandResult Benchmark(uint physicalPartitionNumber, FirehoseBenchmarkMode mode,
        uint trials = 1, CancellationToken cancellationToken = default)
    {
        using var operation = EnterConnected();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ValidateLun(physicalPartitionNumber);
        cancellationToken.ThrowIfCancellationRequested();
        return _storage!.Benchmark(physicalPartitionNumber, trials,
            testDigestPerformance: mode == FirehoseBenchmarkMode.Digest,
            testWritePerformance: mode == FirehoseBenchmarkMode.Write,
            testReadPerformance: mode == FirehoseBenchmarkMode.Read,
            cancellationToken: cancellationToken);
    }

    public byte[] GetSha256Digest(uint physicalPartitionNumber, long startSector, long sectorCount,
        FirehoseIoOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var operation = EnterConnected();
        ValidateLun(physicalPartitionNumber);
        ValidateCachedSectorRange(physicalPartitionNumber, startSector, sectorCount);
        cancellationToken.ThrowIfCancellationRequested();
        return _storage!.GetSha256Digest(physicalPartitionNumber, startSector, sectorCount,
            options, cancellationToken);
    }

    public long Peek(ulong address, long length, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException(Strings.Firehose_DestinationStreamNotWritable, nameof(destination));
        ValidateMemoryRange(address, length);
        using var operation = EnterConnected();
        ct.ThrowIfCancellationRequested();
        return _storage!.Peek(address, length, destination, ct);
    }

    public long Poke(ulong address, IDataSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        long length = source.Length;
        ValidateMemoryRange(address, length);
        using var operation = EnterConnected();
        ct.ThrowIfCancellationRequested();
        return _storage!.Poke(address, source, ct);
    }

    public FirehoseCommandResult FirmwareWrite(uint physicalPartitionNumber, IDataSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        long length = source.Length;
        if (length is <= 0 or > FirehoseConstants.MaximumRawTransferLength)
            throw new ArgumentOutOfRangeException(nameof(source));
        using var operation = EnterConnected();
        ValidateLun(physicalPartitionNumber);
        ct.ThrowIfCancellationRequested();
        return _storage!.FirmwareWrite(physicalPartitionNumber, source, ct);
    }

    private static void ValidateMemoryRange(ulong address, long length)
    {
        if (length <= 0 || (ulong)length > ulong.MaxValue - address)
            throw new ArgumentOutOfRangeException(nameof(length));
    }
}
