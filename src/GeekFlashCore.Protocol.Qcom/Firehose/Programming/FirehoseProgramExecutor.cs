using System.Globalization;
using GeekFlashCore.Protocol.Qcom.Firehose.Storage;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

internal sealed class FirehoseProgramExecutor
{
    private readonly FirehoseSession _session;
    private readonly int _transferBufferSize;
    private readonly IFirehoseStoragePolicy? _policy;
    private readonly FirehoseProgramWriter _writer;

    public FirehoseProgramExecutor(FirehoseSession session, int transferBufferSize,
        IFirehoseStoragePolicy? policy = null,
        Func<(string PublicKey, string Token)?>? onePlusTokenFactory = null,
        FirehoseProgramWriter? writer = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (transferBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(transferBufferSize));
        _transferBufferSize = transferBufferSize;
        _policy = policy;
        _writer = writer ?? new FirehoseProgramWriter(session, FirehoseProgramWriteMode.Auto, policy,
            () => onePlusTokenFactory?.Invoke());
    }

    public long Execute(
        FirehoseProgramRequest request,
        Stream source,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        _writer.ValidateOptions(request.IoOptions);
        using FirehoseProgramPlan plan = FirehoseProgramPlanner.Create(request, source, cancellationToken);
        var mappedSegments = new IReadOnlyList<FirehoseStorageRange>[plan.Segments.Count];
        for (int index = 0; index < plan.Segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FirehoseProgramSegment segment = plan.Segments[index];
            IReadOnlyList<FirehoseStorageRange>? ranges = _policy is null
                ? [new FirehoseStorageRange(
                    segment.StartSector,
                    segment.SectorCount,
                    request.Label,
                    request.FileName)]
                : _policy.Map(
                    request.PhysicalPartitionNumber,
                    segment.StartSector,
                    segment.SectorCount,
                    true,
                    request.Label,
                    request.FileName);
            mappedSegments[index] = FirehoseStorageRangeValidator.Validate(
                ranges,
                segment.StartSector,
                segment.SectorCount);
        }

        if (progress is ITransferProgress transfer)
        {
            long total = 0;
            foreach (var segment in plan.Segments)
                total = checked(total + segment.SectorCount * request.SectorSizeInBytes);
            transfer.Start(total);
        }
        long completed = 0;
        try
        {
            for (int index = 0; index < plan.Segments.Count; index++)
            {
                FirehoseProgramSegment segment = plan.Segments[index];
                using Stream segmentSource = segment.OpenRead(plan.Source);
                long sourceRemaining = segment.SourceLength;
                foreach (FirehoseStorageRange range in mappedSegments[index])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProgramCommand command = CreateCommand(request, range);
                    long wireLength = checked(range.SectorCount * request.SectorSizeInBytes);
                    long sourceLength = Math.Min(sourceRemaining, wireLength);
                    var segmentProgress = progress is null ? null : new AggregateProgress(progress, completed);
                    bool patch = _writer.Begin(command, cancellationToken);
                    long written = patch
                        ? _writer.WritePatch(command, segmentSource, sourceLength, wireLength,
                            request.PaddingByte, segmentProgress, cancellationToken)
                        : _session.SendRaw(segmentSource, sourceLength, wireLength,
                            _transferBufferSize, request.PaddingByte, segmentProgress, cancellationToken).BytesTransferred;
                    sourceRemaining -= sourceLength;
                    completed = checked(completed + written);
                    if (!patch) _policy?.CommandCompleted();
                }
            }
        }
        catch
        {
            // Opening a later sparse region or entering the next mapped range can
            // fail between PATCH transfers, after earlier bytes were acknowledged.
            if (_writer.UsesPatch && completed > 0) _session.Invalidate();
            throw;
        }
        (progress as ITransferProgress)?.Complete(completed);
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
