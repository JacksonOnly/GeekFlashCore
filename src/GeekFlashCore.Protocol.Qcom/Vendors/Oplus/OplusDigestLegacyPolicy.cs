using System.Collections;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;
using GeekFlashCore.Protocol.Qcom.Firehose.Storage;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

/// <summary>Rector Legacy packet counting, numeric sector windows and bounded signature recovery.</summary>
public sealed class OplusDigestLegacyPolicy : IFirehoseStoragePolicy
{
    private readonly IDataSource _digest;
    private readonly long _digestLength;
    private readonly int _fixedSectorCount;
    private readonly int _transferBufferSize;
    private OplusDigestCommandCounter _counter;
    private readonly OplusDigestConfiguration _configuration;
    private readonly string _nopXml;
    private FirehoseSession? _attachedSession;

    /// <param name="digest">A reopenable source retained by the caller for the entire session.</param>
    public OplusDigestLegacyPolicy(OplusDigestIndex index, IDataSource digest,
        OplusDigestConfiguration configuration, int transferBufferSize)
        : this(digest, configuration, transferBufferSize)
    {
        ArgumentNullException.ThrowIfNull(index);
    }

    /// <summary>Creates a Legacy flow from an opaque signed table, without Pt partition metadata.</summary>
    /// <param name="digest">A reopenable source owned by the caller for the entire session.</param>
    /// <param name="configuration">The packet capacity, initial count and exact confirmation NOP.</param>
    /// <param name="transferBufferSize">The negotiated outgoing payload limit.</param>
    public OplusDigestLegacyPolicy(IDataSource digest, OplusDigestConfiguration configuration, int transferBufferSize)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        if (configuration.Mode != OplusDigestMode.OplusDigestLegacy)
            throw new ArgumentException(Strings.Qcom_OplusLegacyModeRequired, nameof(configuration));
        ArgumentNullException.ThrowIfNull(digest);
        if (digest.Length is <= 0 or > OplusDigestParser.MaximumDigestLength)
            throw new OplusDigestException(Strings.Qcom_OplusLegacyLengthInvalid);
        if (transferBufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(transferBufferSize));
        _digest = digest;
        _digestLength = digest.Length;
        _fixedSectorCount = configuration.FixedSectorCount;
        _transferBufferSize = transferBufferSize;
        _configuration = configuration;
        _nopXml = configuration.NopXml ?? FirehoseLegacyXml.DefaultNop;
        FirehoseLegacyXml.ValidateNop(_nopXml);
        _counter = new OplusDigestCommandCounter(configuration.MaxCommandsBeforeDigest, configuration.InitialPacketCount);
    }

    internal void Attach(FirehoseSession session)
    {
        if (ReferenceEquals(_attachedSession, session)) return;
        if (_attachedSession is not null) throw new InvalidOperationException(Strings.Qcom_LegacyPolicyAlreadyAttached);
        _counter = session.ConfigureLegacyWire(_configuration, _counter);
        session.SetLegacyBeforeCommand(CheckDigest);
        _attachedSession = session;
    }

    public IReadOnlyList<FirehoseStorageRange> Map(uint physicalPartitionNumber, long startSector,
        long sectorCount, bool write, string? label = null, string? fileName = null) =>
        _fixedSectorCount == 0
            ? [new FirehoseStorageRange(startSector, sectorCount, label, fileName)]
            : new FixedRanges([new FirehoseStorageRange(startSector, sectorCount, label, fileName)], _fixedSectorCount);

    public FirehoseCommandResult ExecuteCommand(FirehoseSession session, BaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        Attach(session);
        if (command is not ProgramCommand)
            return session.Execute(command, expectedRawMode: command is not PatchCommand, cancellationToken: cancellationToken);
        try
        {
            FirehoseCommandResult result = session.Execute(
                command,
                expectedRawMode: true,
                cancellationToken: cancellationToken);
            return result;
        }
        catch (FirehoseNakException exception) when (session.State != FirehoseSessionState.Faulted &&
            !exception.Result.RawMode && IsSignatureFailure(exception.Result))
        {
            Refresh(session, cancellationToken);
            // Deliberately outside the try: a second NAK is returned immediately, without recovery.
        }
        cancellationToken.ThrowIfCancellationRequested();
        FirehoseCommandResult replay = session.Execute(
            command,
            expectedRawMode: true,
            cancellationToken: cancellationToken);
        return replay;
    }

    public void CommandCompleted()
    {
        // The wire executor counts XML and complete outgoing payloads before ACKs.
    }

    private void CheckDigest(FirehoseSession session, FirehoseCommandExecutor executor, CancellationToken cancellationToken)
    {
        try
        {
            if (_counter.Current > _counter.Trigger)
                throw new OplusDigestException(Strings.Qcom_LegacyPacketCountInvalid);
            if (!_counter.RequiresDigest) return;
            Log.Debug(Strings.Qcom_LogLegacyDigestBoundary, _counter.Current, _configuration.MaxCommandsBeforeDigest,
                _configuration.MaxCommandsBeforeDigest - _counter.Current);
            while (_counter.Current < _counter.Trigger)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The session already holds its operation lease. These executor calls must
                // bypass the public command entry and its table preflight.
                try { executor.ExecuteXml(_nopXml, expectedRawMode: false, cancellationToken: cancellationToken); }
                catch (FirehoseNakException exception) when (_counter.Current == _counter.Trigger &&
                    !exception.Result.RawMode && IsDigestRequested(exception.Result))
                {
                    // Rector intentionally accepts the table-trigger NAK at max_count + 1.
                }
            }
            Refresh(session, cancellationToken, executor);
        }
        catch { session.Invalidate(); throw; }
    }

    private void Refresh(FirehoseSession session, CancellationToken cancellationToken, FirehoseCommandExecutor? executor = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Stream source = _digest.OpenStream() ??
                throw new OplusDigestException(Strings.Qcom_OplusLegacyStreamMissing);
            if (!source.CanRead || _digest.Length != _digestLength)
                throw new OplusDigestException(Strings.Qcom_OplusLegacySourceChanged);
            FirehoseCommandResult? response = executor is null
                ? session.SendLegacyDigest(source, _digestLength, _transferBufferSize,
                    _configuration.DigestResponseTimeoutMilliseconds, cancellationToken)
                : executor.SendLegacyDigest(source, _digestLength, _transferBufferSize,
                    _configuration.DigestResponseTimeoutMilliseconds, cancellationToken);
            bool independentAck = response?.Attributes.TryGetValue("value", out string? value) == true &&
                string.Equals(value, "ACK", StringComparison.OrdinalIgnoreCase);
            if (independentAck) _counter.Reset();
            cancellationToken.ThrowIfCancellationRequested();
            if (executor is null) session.ConfirmLegacyNop(_nopXml, requireHandler: !independentAck, cancellationToken);
            else executor.ConfirmLegacyNop(_nopXml, !independentAck, session.LegacyConfirmationTimeout, cancellationToken);
            if (!independentAck) _counter.Reset(1);
        }
        catch
        {
            session.Invalidate();
            throw;
        }
    }

    private static bool IsSignatureFailure(FirehoseCommandResult result) => result.Logs.Any(log =>
        log.Message.Contains("Verifying signature failed with", StringComparison.Ordinal));

    private static bool IsDigestRequested(FirehoseCommandResult result) => IsSignatureFailure(result) || result.Logs.Any(log =>
        log.Message.Contains("Hash of new table doesn't match the expected hash", StringComparison.Ordinal));

    // Window enumeration remains constant-memory even when a mapped entry spans a large image.
    private sealed class FixedRanges : IReadOnlyList<FirehoseStorageRange>, IValidatedFirehoseRangeSequence
    {
        private readonly IReadOnlyList<FirehoseStorageRange> _ranges;
        private readonly int _sectors;

        public FixedRanges(IReadOnlyList<FirehoseStorageRange> ranges, int sectors)
        {
            _ranges = ranges;
            _sectors = sectors;
            long count = 0;
            foreach (FirehoseStorageRange range in ranges)
                count = checked(count + (range.SectorCount - 1) / sectors + 1);
            if (count > int.MaxValue)
                throw new OplusDigestException(Strings.Qcom_OplusLegacyCommandCountExceeded);
            Count = (int)count;
        }

        public int Count { get; }

        public void ValidateCoverage(long startSector, long sectorCount) =>
            FirehoseStorageRangeValidator.Validate(_ranges, startSector, sectorCount);

        public FirehoseStorageRange this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                foreach (FirehoseStorageRange range in _ranges)
                {
                    long windows = (range.SectorCount - 1) / _sectors + 1;
                    if (index < windows)
                    {
                        long offset = checked((long)index * _sectors);
                        return range with { StartSector = checked(range.StartSector + offset),
                            SectorCount = Math.Min(_sectors, range.SectorCount - offset) };
                    }
                    index -= (int)windows;
                }
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public IEnumerator<FirehoseStorageRange> GetEnumerator()
        {
            foreach (FirehoseStorageRange range in _ranges)
            {
                long end = checked(range.StartSector + range.SectorCount);
                for (long start = range.StartSector; start < end;)
                {
                    long count = Math.Min(_sectors, end - start);
                    yield return range with { StartSector = start, SectorCount = count };
                    start = checked(start + count);
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
