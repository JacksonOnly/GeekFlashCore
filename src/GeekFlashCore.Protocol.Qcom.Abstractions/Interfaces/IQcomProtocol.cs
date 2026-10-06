using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public interface IQcomProtocol : IProtocol,IBlockDeviceProvider
{
    QcomTargetInfo? TargetInfo { get; }

    SaharaTargetInfo ProbeSahara(IProgress<ProgressRecord>? progress = null);

    void UploadSaharaImages(
        IReadOnlyList<SaharaImageEntry> images,
        IProgress<ProgressRecord>? progress = null,
        CancellationToken cancellationToken = default);

    FirehoseCommandResult ConfigureFirehose(IProgress<ProgressRecord>? progress = null);

    long Program(
        FirehoseProgramRequest request,
        IProgress<ProgressRecord>? progress = null,
        CancellationToken cancellationToken = default);

    long Read(
        FirehoseReadRequest request,
        Stream destination,
        IProgress<ProgressRecord>? progress = null,
        CancellationToken cancellationToken = default);

    FirehoseCommandResult ExecuteFirehoseCommand(BaseCommand command);

    FirehoseCommandResult Nop(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    FirehoseCommandResult SetBootableStorageDrive(uint physicalPartitionNumber,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    FirehoseCommandResult XblGpt(uint physicalPartitionNumber,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    FirehoseCommandResult FixGpt(uint physicalPartitionNumber, bool growLastPartition = true,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    FirehoseCommandResult Patch(uint physicalPartitionNumber, long startSector, uint byteOffset,
        uint sizeInBytes, ulong value, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    FirehoseCommandResult Benchmark(uint physicalPartitionNumber, FirehoseBenchmarkMode mode,
        uint trials = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    byte[] GetSha256Digest(uint physicalPartitionNumber, long startSector, long sectorCount,
        FirehoseIoOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    FirehoseCommandResult ExecuteFirehoseXml(string xml);

    /// <summary>Preflight and execute a rawprogram data document in order using synchronous I/O.</summary>
    /// <param name="xml">XML source owned by the caller. Streams opened by the protocol are disposed.</param>
    /// <param name="imageResolver">Resolve XML filenames to stable, reopenable caller-owned image sources.</param>
    /// <param name="progress">Optional per-command and byte progress.</param>
    /// <param name="cancellationToken">Cancellation checked during preflight and transfer boundaries.</param>
    FirehoseScriptResult ExecuteRawProgram(IDataSource xml, Func<string, IDataSource> imageResolver,
        IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Execute only filename=DISK patch entries from a patches (or data) document.</summary>
    /// <remarks>CRC32 values are validated and calculated by the device. No local image is patched.</remarks>
    FirehoseScriptResult ExecutePatchFile(IDataSource xml, IProgress<ProgressRecord>? progress = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    IReadOnlyList<uint> GetPhysicalPartitions() => throw new NotSupportedException();
    FirehoseStorageInfo GetStorageInfo(uint physicalPartitionNumber) => throw new NotSupportedException();
    Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(uint physicalPartitionNumber,
        IProgress<ProgressRecord>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    long Peek(ulong address, long length, Stream destination, CancellationToken ct = default) => throw new NotSupportedException();
    long Poke(ulong address, IDataSource source, CancellationToken ct = default) => throw new NotSupportedException();
    FirehoseCommandResult FirmwareWrite(uint physicalPartitionNumber, IDataSource source,
        CancellationToken ct = default) => throw new NotSupportedException();
}
