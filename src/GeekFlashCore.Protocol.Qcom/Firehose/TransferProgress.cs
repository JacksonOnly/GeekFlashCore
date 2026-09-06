using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

internal interface ITransferProgress : IProgress<long>
{
    void Start(long total);
    void Complete(long current);
}

internal sealed class TransferProgress(IProgress<ProgressRecord> target, string label) : ITransferProgress
{
    private long _total;
    public void Start(long total)
    {
        _total = total;
        Publish(0, ProgressPhase.Started);
    }
    public void Report(long current) => Publish(current, ProgressPhase.Running);
    public void Complete(long current) => Publish(current, ProgressPhase.Completed);
    private void Publish(long current, ProgressPhase phase) =>
        target.Report(new(_total, current, label) { Unit = ProgressUnit.Bytes, Phase = phase });
}
