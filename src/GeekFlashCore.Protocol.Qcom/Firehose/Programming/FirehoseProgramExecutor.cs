using System.Globalization;
using GeekFlashCore.Protocol.Qcom.Firehose.Storage;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

internal sealed class FirehoseProgramExecutor
{
    private readonly FirehoseSession _session;
    private readonly int _transferBufferSize;
    private readonly IFirehoseStoragePolicy? _policy;

    public FirehoseProgramExecutor(FirehoseSession session, int transferBufferSize, IFirehoseStoragePolicy? policy = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (transferBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(transferBufferSize));
        _transferBufferSize = transferBufferSize;
        _policy = policy;
    }

    public long Execute(
        FirehoseProgramRequest request,
        Stream source,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using FirehoseProgramPlan plan = FirehoseProgramPlanner.Create(request, source, cancellationToken);
        // Validate every mapping before the first command, but do not retain all
        // mapped ranges for the lifetime of a large Sparse image.
        if (_policy is not null)
        {
            foreach (FirehoseProgramSegment segment in plan.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<FirehoseStorageRange> ranges = _policy.Map(
                    request.PhysicalPartitionNumber, segment.StartSector, segment.SectorCount,
                    true, request.Label, request.FileName);
                if (ranges is null || ranges.Count == 0)
                    throw new InvalidDataException(Strings.Qcom_InvalidResource);
            }
        }

        long completed = 0;
        foreach (FirehoseProgramSegment segment in plan.Segments)
        {
            using Stream segmentSource = segment.OpenRead(plan.Source);
            long sourceRemaining = segment.SourceLength;
            IReadOnlyList<FirehoseStorageRange> ranges = _policy?.Map(
                request.PhysicalPartitionNumber, segment.StartSector, segment.SectorCount,
                true, request.Label, request.FileName) ??
                [new FirehoseStorageRange(segment.StartSector, segment.SectorCount, request.Label, request.FileName)];
            foreach (FirehoseStorageRange range in ranges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProgramCommand command = CreateCommand(request, range);
                if (_policy is null) _session.Execute(command, expectedRawMode: true);
                else _policy.ExecuteCommand(_session, command, cancellationToken);
                long wireLength = checked(range.SectorCount * request.SectorSizeInBytes);
                long sourceLength = Math.Min(sourceRemaining, wireLength);
                var segmentProgress = progress is null ? null : new AggregateProgress(progress, completed);
                FirehoseCommandResult result = _session.SendRaw(segmentSource, sourceLength, wireLength,
                    _transferBufferSize, request.PaddingByte, segmentProgress, cancellationToken);
                sourceRemaining -= sourceLength;
                completed = checked(completed + result.BytesTransferred);
                _policy?.CommandCompleted();
            }
        }
        return completed;
    }

    private static ProgramCommand CreateCommand(
        FirehoseProgramRequest request,
        FirehoseStorageRange segment) => new()
    {
        Storage = request.Storage,
        Slot = request.Slot,
        PhysicalPartitionNumber = request.PhysicalPartitionNumber,
        SectorSizeInBytes = request.SectorSizeInBytes,
        NumPartitionSectors = segment.SectorCount.ToString(CultureInfo.InvariantCulture),
        StartSector = segment.StartSector.ToString(CultureInfo.InvariantCulture),
        Label = segment.Label,
        FileName = segment.FileName,
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
