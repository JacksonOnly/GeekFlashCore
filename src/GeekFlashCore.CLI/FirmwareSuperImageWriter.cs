using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Firmware;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.CLI;

internal static class FirmwareSuperImageWriter
{
    internal static async Task<long> WriteAsync(IProtocol protocol, FirmwareSuperImagePlan plan, StorageTarget target,
        IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        if (protocol is not (IQcomProtocol or IMtkProtocol)) throw new NotSupportedException(Strings.Cli_FirmwareSuperProtocolUnsupported);
        uint block, physical; long? deviceCapacity; MtkStorageInfo? mtkStorage = null;
        if (protocol is IMtkProtocol mtk)
        {
            var storage = mtkStorage = mtk.GetStorageInfo(); physical = target.PhysicalPartitionNumber ?? storage.UserRegionId;
            var region = storage.Regions.SingleOrDefault(r => r.WireId == physical);
            if (region is null || target is not PartitionTarget && !region.CanWrite) throw Invalid(); block = checked((uint)region.BlockSize); deviceCapacity = region.Length;
        }
        else
        {
            block = StorageCommands.SectorSize(protocol); physical = target.PhysicalPartitionNumber ?? 0;
            deviceCapacity = Capacity((IQcomProtocol)protocol, physical, block);
        }
        long offset, capacity;
        if (target is PartitionTarget named)
        {
            var partitions = protocol is IQcomProtocol q && named.PhysicalPartitionNumber is { } selected
                ? await q.GetPartitionsAsync(selected, progress, ct).ConfigureAwait(false) : await protocol.GetPartitionsAsync(progress, ct).ConfigureAwait(false);
            var matches = partitions.Where(p => string.Equals(p.Name, named.Name, protocol is IQcomProtocol ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) &&
                (target.PhysicalPartitionNumber is null || StorageCommands.PartitionLun(p) == physical)).ToArray();
            if (matches.Length != 1) throw new ArgumentException(Strings.FormatCli_PartitionNotUnique(named.Name));
            physical = StorageCommands.PartitionLun(matches[0]); offset = matches[0].Offset ?? throw Invalid(); capacity = matches[0].Length ?? throw Invalid();
            if (protocol is IQcomProtocol qcom) deviceCapacity = Capacity(qcom, physical, block);
            else
            {
                var region = mtkStorage!.Regions.SingleOrDefault(r => r.WireId == physical);
                if (region is null || !region.CanWrite) throw Invalid(); block = checked((uint)region.BlockSize); deviceCapacity = region.Length;
            }
        }
        else if (target is SectorTarget sectors)
        {
            if (sectors.SectorSize != block) throw Invalid(); offset = checked(sectors.StartSector * block); capacity = checked(sectors.SectorCount * block);
        }
        else if (target is OffsetTarget bytes) { offset = bytes.StartOffset; capacity = bytes.Length; }
        else throw Invalid();
        ct.ThrowIfCancellationRequested();
        if (offset < 0 || offset > long.MaxValue - plan.LogicalLength || capacity < plan.LogicalLength || offset % block != 0 || plan.Layout.Geometry.LogicalBlockSize % block != 0 ||
            deviceCapacity is { } available && (offset > available || capacity > available - offset)) throw Invalid();
        string label = target is PartitionTarget p ? p.Name : "super";
        progress.Report(new(0, 0, label) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Started });
        long completed = 0, lastReport = Environment.TickCount64; bool attempted = false; long written;
        try
        {
            written = plan.Write((region, source) =>
            {
                ct.ThrowIfCancellationRequested(); long start = checked(offset + region.OutputOffset); string regionLabel = label + "/" + region.PartitionName;
                using var tracked = new RegionProgressSource(source, n =>
                {
                    long now = Environment.TickCount64;
                    if (n != region.Length && now - lastReport < 100) return;
                    lastReport = now; progress.Report(new(0, checked(completed + n), regionLabel) { Unit = ProgressUnit.Bytes });
                });
                attempted = true;
                if (protocol is IQcomProtocol qcom)
                {
                    long count = qcom.Program(new() { Source = tracked, Format = FirehoseProgramFormat.Raw, PhysicalPartitionNumber = physical,
                        StartSector = start / block, SectorCount = region.Length / block, SectorSizeInBytes = block, Label = label, FileName = plan.Name }, cancellationToken: ct);
                    if (count != region.Length) throw Invalid();
                }
                else
                {
                    using var stream = tracked.OpenStream(); ((IMtkProtocol)protocol).Write(new(physical, start, region.Length), stream, ct);
                }
                completed = checked(completed + region.Length);
            }, ct);
        }
        catch
        {
            // A late chunk/CRC failure occurs between protocol calls; retire that partial-write session too.
            if (attempted)
            {
                try { if (protocol.IsConnected) await protocol.DisconnectAsync(ct: CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { Serilog.Log.Error(Strings.Cli_FirmwareSuperCleanupFailed, cleanup.GetType().Name); }
            }
            throw;
        }
        progress.Report(new(written, written, label) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Completed });
        return written;
    }
    private static long? Capacity(IQcomProtocol protocol, uint lun, uint block)
    {
        var count = protocol.TargetInfo?.Firehose?.StorageInfos.FirstOrDefault(s => s.PhysicalPartitionNumber == lun)?.BlockCount;
        return count is { } value && value <= (ulong)(long.MaxValue / block) ? checked((long)value * block) : null;
    }
    private static InvalidOperationException Invalid() => new(Strings.Cli_FirmwareSuperTargetInvalid);
    private sealed class RegionProgressSource(IDataSource source, Action<long> progress) : IDataSource, IDisposable
    {
        private TrackingStream? _stream;
        public long Length => source.Length;
        public Stream OpenStream() => _stream = new(source.OpenStream(), progress);
        public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(OpenStream()); }
        public void Dispose() => _stream?.Dispose();
        private sealed class TrackingStream(Stream source, Action<long> progress) : Stream
        {
            private long _count;
            public override bool CanRead => source.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => source.Length;
            public override long Position { get => _count; set => throw new NotSupportedException(Strings.Cli_FirmwareSuperTargetInvalid); }
            public override int Read(Span<byte> buffer) { int n = source.Read(buffer); _count += n; if (n != 0) progress(_count); return n; }
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(Strings.Cli_FirmwareSuperTargetInvalid);
            public override void SetLength(long value) => throw new NotSupportedException(Strings.Cli_FirmwareSuperTargetInvalid);
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Strings.Cli_FirmwareSuperTargetInvalid);
            protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        }
    }
}
