using System.Globalization;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

internal sealed class FirehoseProgramExecutor
{
    private readonly FirehoseSession _session;
    private readonly int _transferBufferSize;

    public FirehoseProgramExecutor(FirehoseSession session, int transferBufferSize)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (transferBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(transferBufferSize));
        _transferBufferSize = transferBufferSize;
    }

    public long Execute(
        FirehoseProgramRequest request,
        Stream source,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using FirehoseProgramPlan plan = FirehoseProgramPlanner.Create(request, source, cancellationToken);
        long completed = 0;
        foreach (FirehoseProgramSegment segment in plan.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _session.Execute(CreateCommand(request, segment), expectedRawMode: true);
            using Stream segmentSource = segment.OpenRead(plan.Source);
            long wireLength = checked(segment.SectorCount * request.SectorSizeInBytes);
            var segmentProgress = progress is null ? null : new AggregateProgress(progress, completed);
            FirehoseCommandResult result = _session.SendRaw(
                segmentSource,
                segment.SourceLength,
                wireLength,
                _transferBufferSize,
                request.PaddingByte,
                segmentProgress,
                cancellationToken);
            completed = checked(completed + result.BytesTransferred);
        }
        return completed;
    }

    private static ProgramCommand CreateCommand(
        FirehoseProgramRequest request,
        FirehoseProgramSegment segment) => new()
    {
        Storage = request.Storage,
        Slot = request.Slot,
        PhysicalPartitionNumber = request.PhysicalPartitionNumber,
        SectorSizeInBytes = request.SectorSizeInBytes,
        NumPartitionSectors = segment.SectorCount.ToString(CultureInfo.InvariantCulture),
        StartSector = segment.StartSector.ToString(CultureInfo.InvariantCulture),
        Label = request.Label,
        FileName = request.FileName,
        LastSector = request.IoOptions.LastSector,
        SkipBadBlock = ToByte(request.IoOptions.SkipBadBlock),
        GetSpare = ToByte(request.IoOptions.GetSpare),
        EccDisabled = ToByte(request.IoOptions.EccDisabled)
    };

    private static byte? ToByte(bool? value) => value is null ? null : value.Value ? (byte)1 : (byte)0;

    private sealed class AggregateProgress(IProgress<long> target, long origin) : IProgress<long>
    {
        public void Report(long value) => target.Report(checked(origin + value));
    }
}
