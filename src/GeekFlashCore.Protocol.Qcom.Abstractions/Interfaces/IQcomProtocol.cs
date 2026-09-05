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

    FirehoseCommandResult ExecuteFirehoseXml(string xml);

    IReadOnlyList<uint> GetPhysicalPartitions() => throw new NotSupportedException();
    FirehoseStorageInfo GetStorageInfo(uint physicalPartitionNumber) => throw new NotSupportedException();
    Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(uint physicalPartitionNumber,
        IProgress<ProgressRecord>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    long Peek(ulong address, long length, Stream destination, CancellationToken ct = default) => throw new NotSupportedException();
    long Poke(ulong address, IDataSource source, CancellationToken ct = default) => throw new NotSupportedException();
    FirehoseCommandResult FirmwareWrite(uint physicalPartitionNumber, IDataSource source,
        CancellationToken ct = default) => throw new NotSupportedException();
}
