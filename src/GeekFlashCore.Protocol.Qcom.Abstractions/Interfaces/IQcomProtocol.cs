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
}
