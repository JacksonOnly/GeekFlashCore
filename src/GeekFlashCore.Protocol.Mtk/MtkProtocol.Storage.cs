using GeekFlashCore.Android.Sparse;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol
{
    private List<(string Name, MtkFlashRange Range)>? _partitions;
    private MtkFlashRange Resolve(StorageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Ready();
        uint id = target.PhysicalPartitionNumber ?? _storage!.UserRegionId;
        var region = _storage!.Regions.SingleOrDefault(r => r.WireId == id) ?? throw new MtkCapabilityException("storage region");
        switch (target)
        {
            case SectorTarget sectors:
                if (sectors.StartSector < 0 || sectors.SectorCount <= 0 || sectors.SectorSize != region.BlockSize)
                    throw new ArgumentOutOfRangeException(nameof(target));
                return new(id, checked(sectors.StartSector * sectors.SectorSize), checked(sectors.SectorCount * sectors.SectorSize));
            case OffsetTarget bytes:
                return new(id, bytes.StartOffset, bytes.Length);
            case PartitionTarget partition:
                var found = LoadPartitionsCore().Where(p => MtkPartitionNames.Matches(p.Name, partition.Name) &&
                    (target.PhysicalPartitionNumber is null || p.Range.RegionId == id)).ToArray();
                if (found.Length != 1)
                    throw new MtkResourceException("partition name/region");
                return found[0].Range;
            default:
                throw new ArgumentException(nameof(target));
        }
    }
    public Task<ReadDestination> ReadAsync(ReadDestination destination, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Task.FromResult(Execute(() =>
        {
            var range = Resolve(destination.Target);
            var region = Range(range);
            if (!destination.OutputStream.CanWrite)
                throw new ArgumentException(nameof(destination));
            var tracker = new TransferProgress(progress, range.Length, "read");
            tracker.Report(0, ProgressPhase.Started);
            using var output = new ProgressStream(destination.OutputStream, n => tracker.Report(n));
            _da!.Read(region, range.Offset, range.Length, output);
            _wire.Check();
            tracker.Report(range.Length, ProgressPhase.Completed);
            return destination;
        }, ct));
    }
    public Task<long> WriteAsync(WriteSource source, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Task.FromResult(Execute(() =>
        {
            var range = Resolve(source.Target);
            var region = Range(range);
            if (source.Source.Length <= 0)
                throw new MtkResourceException("write source length");
            using Stream input = source.Source.OpenStream();
            if (!input.CanRead || input.CanSeek && (input.Position != 0 || input.Length != source.Source.Length))
                throw new MtkResourceException("write source");
            using var prefix = input.CanSeek ? null : new PrefixStream(input, source.Source.Length);
            Stream content = prefix ?? input;
            long written;
            TransferProgress tracker;
            if (SparseImageParser.IsSparse(content))
            {
                using var block = new StreamBlockDevice(content, source.Source.Length, DeviceOwnership.Borrow);
                using var sparse = SparseImageParser.Open(block, DeviceOwnership.Borrow);
                if (sparse.ExpandedLength <= 0 || sparse.ExpandedLength > range.Length || sparse.Header.BlockSize % region.BlockSize != 0)
                    throw new MtkResourceException("Sparse geometry");
                sparse.VerifyChecksum(cancellationToken: ct);
                var data = sparse.CreateDataRegions();
                // Validate every expanded region before the first storage write.
                foreach (var part in data)
                    _ = Range(new(range.RegionId, checked(range.Offset + part.StartBlock * (long)sparse.Header.BlockSize), part.Length));
                tracker = new(progress, sparse.ExpandedLength, "write");
                tracker.Report(0, ProgressPhase.Started);
                foreach (var part in data)
                {
                    long start = checked(part.StartBlock * (long)sparse.Header.BlockSize);
                    tracker.Report(start);
                    using var partStream = part.OpenRead(content, true);
                    using var tracked = new ProgressStream(partStream, n => tracker.Report(checked(start + n)));
                    _da!.Write(region, checked(range.Offset + start), part.Length, tracked);
                }
                written = sparse.ExpandedLength;
            }
            else
            {
                written = source.Source.Length;
                long padded = checked((written + region.BlockSize - 1) / region.BlockSize * region.BlockSize);
                if (padded > range.Length)
                    throw new ArgumentOutOfRangeException(nameof(source));
                tracker = new(progress, written, "write");
                tracker.Report(0, ProgressPhase.Started);
                using var tracked = new ProgressStream(content, n => tracker.Report(n));
                using var padding = new PaddingStream(tracked, written, padded);
                _da!.Write(region, range.Offset, padded, padding);
            }
            _partitions = null;
            _wire.Check();
            tracker.Report(written, ProgressPhase.Completed);
            return written;
        }, ct));
    }
    public Task<bool> EraseAsync(StorageTarget target, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Execute(() =>
        {
            var range = Resolve(target); var region = Range(range);
            var tracker = new TransferProgress(progress, range.Length, "erase");
            tracker.Report(0, ProgressPhase.Started);
            _wire.ProgressPercent = percent => tracker.Report(checked(range.Length / 100 * percent + range.Length % 100 * percent / 100));
            try { _da!.Erase(region, range.Offset, range.Length); }
            finally { _wire.ProgressPercent = null; }
            _partitions = null; _wire.Check();
            tracker.Report(range.Length, ProgressPhase.Completed); return true;
        }, ct));
    public Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Execute<IReadOnlyList<PartitionInfo>>(() => LoadPartitionsCore().Select(p => new PartitionInfo(p.Name, p.Range.Offset, p.Range.Offset, p.Range.Length,
            new Dictionary<string, string>
            {
                { "PhysicalPartitionNumber", p.Range.RegionId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                { "NativePartitionName", MtkPartitionNames.Wire(p.Name) }
            })).ToArray(), ct));
    private List<(string Name, MtkFlashRange Range)> LoadPartitionsCore()
    {
        Ready();
        if (_partitions is not null)
            return _partitions;
        List<(string, MtkFlashRange)> result = [];
        foreach (var region in _storage!.Regions)
        {
            if (region.Kind is MtkStorageKind.Emmc or MtkStorageKind.Ufs && region.WireId is 1 or 2)
                result.Add((region.WireId == 1 ? MtkPartitionNames.Preloader : MtkPartitionNames.PreloaderBackup,
                    new MtkFlashRange(region.WireId, 0, region.Length)));
            var entries = ReadGpt(region);
            if (entries is null)
            {
                if (region.WireId == _storage.UserRegionId && _da is Da.LegacySession &&
                    (_options.LegacyPmtLayout is not null || region.Kind == MtkStorageKind.Emmc && region.BlockSize == 512 && region.Length >= 0x100000))
                    result.AddRange(ReadLegacyPmt(_options.LegacyPmtLayout ?? MtkPmtLayout.DiskV1)
                        .Select(p => (MtkPartitionNames.Display(p.Name!), new MtkFlashRange(region.WireId, p.Offset!.Value, p.Length!.Value))));
                else if (region.WireId == _storage.UserRegionId && region.Kind == MtkStorageKind.Nand && _da is Da.XmlSession)
                    result.AddRange(ReadXmlPartitionTable().Select(p => (MtkPartitionNames.Display(p.Name!), new MtkFlashRange(region.WireId, p.Offset!.Value, p.Length!.Value))));
                continue;
            }
            if (region.WireId == _storage.UserRegionId)
                result.Add((MtkPartitionNames.PrimaryGpt, entries.Primary));
            foreach (var entry in entries.Entries)
            {
                var range = new MtkFlashRange(region.WireId, checked((long)entry.FirstLba * region.BlockSize), checked((long)entry.SectorCount * region.BlockSize));
                _ = Range(range);
                result.Add((MtkPartitionNames.Display(entry.Name), range));
            }
            if (region.WireId == _storage.UserRegionId)
                result.Add((MtkPartitionNames.BackupGpt, entries.Backup));
        }
        return _partitions = result;
    }
    public IReadOnlyList<BlockDeviceDescriptor> GetBlockDevices() => Execute<IReadOnlyList<BlockDeviceDescriptor>>(() =>
    {
        Ready();
        return _storage!.Regions.Select(r => new BlockDeviceDescriptor(new($"mtk:{r.Kind}:{r.WireId}"), r.Length, r.BlockSize, r.CanWrite, r.Kind.ToString(), (int)r.WireId)).ToArray();
    });
    public IReadableBlockDevice OpenBlockDevice(BlockDeviceId id, BlockDeviceOpenOptions? options = null) => Execute<IReadableBlockDevice>(() =>
    {
        Ready();
        var region = _storage!.Regions.SingleOrDefault(r => id.Value == $"mtk:{r.Kind}:{r.WireId}") ?? throw new MtkCapabilityException("block device");
        if (options?.Writable == true && !region.CanWrite) throw new MtkCapabilityException("read-only storage region");
        return new MtkBlockDevice(this, region, Generation, options?.Writable == true);
    });
    private sealed class TransferProgress(IProgress<ProgressRecord>? progress, long total, string phase)
    {
        private long _current;
        private readonly string _label = Strings.FormatPhase(phase);
        public void Report(long current, ProgressPhase state = ProgressPhase.Running)
        {
            _current = Math.Clamp(current, _current, total);
            progress?.Report(new(total, _current, _label) { Unit = ProgressUnit.Bytes, Phase = state });
        }
    }
    // Check a nonseekable source's magic without consuming Raw bytes or treating a Sparse file as Raw.
    private sealed class PrefixStream : Stream
    {
        private readonly Stream _source;
        private readonly byte[] _prefix;
        private int _prefixPosition;
        public PrefixStream(Stream source, long length)
        {
            _source = source;
            _prefix = new byte[(int)Math.Min(4, length)];
            source.ReadExactly(_prefix);
            if (_prefix.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(_prefix) == 0xed26ff3a)
                throw new MtkResourceException("seekable Sparse source");
        }
        public override int Read(Span<byte> data)
        {
            int copied = Math.Min(data.Length, _prefix.Length - _prefixPosition);
            _prefix.AsSpan(_prefixPosition, copied).CopyTo(data); _prefixPosition += copied;
            return copied != 0 ? copied : _source.Read(data);
        }
        public override int Read(byte[] b, int o, int n) => Read(b.AsSpan(o, n));
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }
    // The underlying stream belongs to the source/destination; disposing this wrapper never closes it.
    private sealed class ProgressStream(Stream stream, Action<long> report) : Stream
    {
        private long _transferred;
        private void Advance(int count) { _transferred = checked(_transferred + count); report(_transferred); }
        public override int Read(Span<byte> data) { int count = stream.Read(data); Advance(count); return count; }
        public override int Read(byte[] b, int o, int n) => Read(b.AsSpan(o, n));
        public override void Write(ReadOnlySpan<byte> data) { stream.Write(data); Advance(data.Length); }
        public override void Write(byte[] b, int o, int n) => Write(b.AsSpan(o, n));
        public override bool CanRead => stream.CanRead;
        public override bool CanWrite => stream.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _transferred; set => throw new NotSupportedException(); }
        public override void Flush() => stream.Flush();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
    }
    private sealed class PaddingStream(Stream source, long actual, long padded) : Stream
    {
        private long _position;
        public override int Read(Span<byte> data)
        {
            int total = (int)Math.Min(data.Length, padded - _position);
            if (total == 0)
                return 0;
            int physical = (int)Math.Min(total, Math.Max(0, actual - _position));
            if (physical > 0)
                source.ReadExactly(data[..physical]);
            data.Slice(physical, total - physical).Clear();
            _position += total;
            return total;
        }
        public override int Read(byte[] b, int o, int n) => Read(b.AsSpan(o, n));
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => padded; public override long Position
        {
            get => _position; set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }
    private sealed class MtkBlockDevice(MtkProtocol owner, MtkStorageRegion region, long generation, bool writable) : IWritableBlockDevice, IBlockDeviceFlushDurability
    {
        private bool _disposed;
        public BlockDeviceId Id => new($"mtk:{region.Kind}:{region.WireId}");
        public long Length => region.Length; public int LogicalBlockSize => region.BlockSize;
        public BlockDeviceFlushDurability FlushDurability => BlockDeviceFlushDurability.ProtocolAcknowledged;
        private void Check()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MtkBlockDevice));
            if (generation != owner.Generation)
                throw new InvalidOperationException(Strings.SessionUnavailable);
            owner.Ready();
        }
        public int ReadAt(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            int count = (int)Math.Min(destination.Length, Length - offset), done = 0;
            int window = Math.Max(LogicalBlockSize, owner._options.BufferSize / LogicalBlockSize * LogicalBlockSize);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(window);
            try
            {
                while (done < count)
                {
                    long position = offset + done, start = position / LogicalBlockSize * LogicalBlockSize;
                    int prefix = (int)(position - start), take = Math.Min(count - done, window - prefix);
                    int aligned = checked((prefix + take + LogicalBlockSize - 1) / LogicalBlockSize * LogicalBlockSize);
                    owner.Execute(() => { Check(); using var output = new MemoryStream(buffer, 0, aligned, true); owner._da!.Read(region, start, aligned, output); return 0; });
                    buffer.AsSpan(prefix, take).CopyTo(destination[done..]);
                    done += take;
                }
                if (count == 0)
                    owner.Execute(() => { Check(); return 0; });
                return count;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        }
        public void WriteAt(long offset, ReadOnlySpan<byte> source)
        {
            if (!writable)
                throw new MtkCapabilityException("read-only block device");
            if (offset < 0 || offset % LogicalBlockSize != 0 || source.Length % LogicalBlockSize != 0 || offset > Length - source.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            int window = Math.Max(LogicalBlockSize, owner._options.BufferSize / LogicalBlockSize * LogicalBlockSize);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(window);
            try
            {
                for (int done = 0; done < source.Length;)
                {
                    int n = Math.Min(window, source.Length - done);
                    source.Slice(done, n).CopyTo(buffer);
                    long position = offset + done;
                    owner.Execute(() => { Check(); using var input = new MemoryStream(buffer, 0, n, false); owner._da!.Write(region, position, n, input); owner._partitions = null; return 0; });
                    done += n;
                }
                if (source.IsEmpty)
                    owner.Execute(() => { Check(); return 0; });
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        }
        public void Flush() => owner.Execute(() => { Check(); return 0; });
        public void Dispose() => _disposed = true;
    }
}
