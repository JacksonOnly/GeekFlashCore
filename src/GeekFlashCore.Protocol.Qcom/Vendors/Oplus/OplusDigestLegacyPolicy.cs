using System.Collections;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;
using GeekFlashCore.Protocol.Qcom.Firehose.Storage;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

/// <summary>Per-session Legacy Digest mapping, fixed sector windows and bounded XML recovery.</summary>
public sealed class OplusDigestLegacyPolicy : IFirehoseStoragePolicy
{
    private readonly OplusDigestPtPolicy _mapping;
    private readonly IDataSource _digest;
    private readonly long _digestLength;
    private readonly int _fixedSectorCount;
    private readonly int _transferBufferSize;
    private readonly OplusDigestCommandCounter _counter;

    /// <param name="digest">A reopenable source retained by the caller for the entire session.</param>
    public OplusDigestLegacyPolicy(OplusDigestIndex index, IDataSource digest,
        OplusDigestConfiguration configuration, int transferBufferSize)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        if (configuration.Mode != OplusDigestMode.OplusDigestLegacy)
            throw new ArgumentException(Strings.Qcom_OplusLegacyModeRequired, nameof(configuration));
        ArgumentNullException.ThrowIfNull(digest);
        if (digest.Length is <= 0 or > OplusDigestParser.MaximumDigestLength)
            throw new OplusDigestException(Strings.Qcom_OplusLegacyLengthInvalid);
        if (transferBufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(transferBufferSize));
        _mapping = new OplusDigestPtPolicy(index);
        _digest = digest;
        _digestLength = digest.Length;
        _fixedSectorCount = configuration.FixedSectorCount;
        _transferBufferSize = transferBufferSize;
        _counter = new OplusDigestCommandCounter(configuration.MaxCommandsBeforeDigest);
    }

    public IReadOnlyList<FirehoseStorageRange> Map(uint physicalPartitionNumber, long startSector,
        long sectorCount, bool write, string? label = null, string? fileName = null) =>
        new FixedRanges(_mapping.Map(physicalPartitionNumber, startSector, sectorCount, write, label, fileName),
            _fixedSectorCount);

    public FirehoseCommandResult ExecuteCommand(FirehoseSession session, BaseCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (_counter.RequiresDigest) Refresh(session, cancellationToken);
        try
        {
            FirehoseCommandResult result = session.Execute(command, expectedRawMode: true);
            _counter.CommandSent();
            return result;
        }
        catch (FirehoseNakException exception) when (!exception.Result.RawMode && IsSignatureFailure(exception.Result))
        {
            // QnQcLIB counts the XML write even when the response is a NAK.
            _counter.CommandSent();
            Refresh(session, cancellationToken, includeNopPreamble: false);
            // Deliberately outside the try: a second NAK is returned immediately, without recovery.
        }
        catch (FirehoseNakException)
        {
            _counter.CommandSent();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        FirehoseCommandResult replay = session.Execute(command, expectedRawMode: true);
        _counter.CommandSent();
        return replay;
    }

    public void CommandCompleted()
    {
        // The counter advances when the XML command is sent, before raw data
        // and its final ACK, matching QnQcLIB's Legacy semantics.
    }

    private void Refresh(FirehoseSession session, CancellationToken cancellationToken, bool includeNopPreamble = true)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Stream source = _digest.OpenStream() ??
                throw new OplusDigestException(Strings.Qcom_OplusLegacyStreamMissing);
            if (!source.CanRead || _digest.Length != _digestLength)
                throw new OplusDigestException(Strings.Qcom_OplusLegacySourceChanged);
            if (includeNopPreamble)
            {
                session.Execute(new NopCommand());
                cancellationToken.ThrowIfCancellationRequested();
                session.Execute(new NopCommand());
            }
            session.SendDigest(source, _digestLength, _transferBufferSize, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            session.Execute(new NopCommand());
            _counter.Reset();
        }
        catch
        {
            session.Invalidate();
            throw;
        }
    }

    private static bool IsSignatureFailure(FirehoseCommandResult result) => result.Logs.Any(log =>
        log.Message.Contains("Verifying signature failed with", StringComparison.OrdinalIgnoreCase));

    // Window enumeration remains constant-memory even when a mapped entry spans a large image.
    private sealed class FixedRanges : IReadOnlyList<FirehoseStorageRange>
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
