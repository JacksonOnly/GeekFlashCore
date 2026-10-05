using GeekFlashCore.Android.Sparse;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Gpt;
using GeekFlashCore.Gpt.Abstractions;
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
                var found = LoadPartitionsCore().Where(p => p.Name.Equals(partition.Name, StringComparison.OrdinalIgnoreCase) &&
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
            _da!.Read(region, range.Offset, range.Length, destination.OutputStream);
            progress?.Report(new(range.Length, range.Length, Strings.FormatPhase("read"))
            {
                Unit = ProgressUnit.Bytes,
                Phase = ProgressPhase.Completed
            });
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
            if (!input.CanRead || input.CanSeek && input.Length - input.Position != source.Source.Length)
                throw new MtkResourceException("write source");
            long written;
            if (SparseImageParser.IsSparse(input))
            {
                using var block = new StreamBlockDevice(input, source.Source.Length, DeviceOwnership.Borrow);
                using var sparse = SparseImageParser.Open(block, DeviceOwnership.Borrow);
                if (sparse.ExpandedLength <= 0 || sparse.ExpandedLength > range.Length || sparse.Header.BlockSize % region.BlockSize != 0)
                    throw new MtkResourceException("Sparse geometry");
                sparse.VerifyChecksum(cancellationToken: ct);
                var data = sparse.CreateDataRegions();
                // Validate every expanded region before the first storage write.
                foreach (var part in data)
                    _ = Range(new(range.RegionId, checked(range.Offset + part.StartBlock * (long)sparse.Header.BlockSize), part.Length));
                foreach (var part in data)
                {
                    using var partStream = part.OpenRead(input, true);
                    _da!.Write(region, checked(range.Offset + part.StartBlock * (long)sparse.Header.BlockSize), part.Length, partStream);
                }
                written = sparse.ExpandedLength;
            }
            else
            {
                written = source.Source.Length;
                long padded = checked((written + region.BlockSize - 1) / region.BlockSize * region.BlockSize);
                if (padded > range.Length)
                    throw new ArgumentOutOfRangeException(nameof(source));
                using var padding = new PaddingStream(input, written, padded);
                _da!.Write(region, range.Offset, padded, padding);
            }
            _partitions = null;
            progress?.Report(new(written, written, Strings.FormatPhase("write"))
            {
                Unit = ProgressUnit.Bytes,
                Phase = ProgressPhase.Completed
            });
            return written;
        }, ct));
    }
    public Task<bool> EraseAsync(StorageTarget target, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Execute(() => { var range = Resolve(target); var region = Range(range); _da!.Erase(region, range.Offset, range.Length); _partitions = null; return true; }, ct));
    public Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Execute<IReadOnlyList<PartitionInfo>>(() => LoadPartitionsCore().Select(p => new PartitionInfo(p.Name, p.Range.Offset, p.Range.Offset, p.Range.Length,
            new Dictionary<string, string> { { "PhysicalPartitionNumber", p.Range.RegionId.ToString(System.Globalization.CultureInfo.InvariantCulture) } })).ToArray(), ct));
    private List<(string Name, MtkFlashRange Range)> LoadPartitionsCore()
    {
        Ready();
        if (_partitions is not null)
            return _partitions;
        List<(string, MtkFlashRange)> result = [];
        foreach (var region in _storage!.Regions)
        {
            int block = region.BlockSize;
            if (region.Length < block * 2L)
                continue;
            byte[] header = new byte[block * 2];
            using (var output = new MemoryStream(header, true))
                _da!.Read(region, 0, header.Length, output);
            if (!header.AsSpan(block, 8).SequenceEqual("EFI PART"u8))
                continue;
            var h = header.AsSpan(block);
            ulong entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(h[72..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(h[80..]), entrySize = BinaryPrimitives.ReadUInt32LittleEndian(h[84..]);
            if (entriesLba != 2 || count is 0 or > 4096 || entrySize is < 128 or > 4096 || entrySize % 8 != 0)
                throw new MtkResourceException("GPT geometry");
            long bytes = checked((long)count * entrySize);
            long padded = checked((bytes + block - 1) / block * block), size = checked(header.Length + padded);
            if (size > 1048576 || size > region.Length)
                throw new MtkResourceException("GPT metadata");
            byte[] image = new byte[(int)size];
            header.CopyTo(image, 0);
            using (var output = new MemoryStream(image, header.Length, (int)padded, true))
                _da!.Read(region, header.Length, padded, output);
            var table = new GptParser().Parse(image, new GptParseOptions
            {
                SectorSize = block,
                CrcPolicy = GptCrcPolicy.Strict,
                AllowUnpatchedPartitionGeometry = false,
                AllowEmptyPartitionTypeId = false,
                SkipEmptyPartitionTypeId = true
            });
            if (table.Header.AlternateLba >= (ulong)(region.Length / block))
                throw new MtkResourceException("GPT disk size");
            foreach (var entry in table.Entries)
            {
                var range = new MtkFlashRange(region.WireId, checked((long)entry.FirstLba * block), checked((long)entry.SectorCount * block));
                _ = Range(range);
                result.Add((entry.Name, range));
            }
        }
        return _partitions = result;
    }
    public IReadOnlyList<BlockDeviceDescriptor> GetBlockDevices() => Execute<IReadOnlyList<BlockDeviceDescriptor>>(() =>
    {
        Ready();
        return _storage!.Regions.Select(r => new BlockDeviceDescriptor(new($"mtk:{r.Kind}:{r.WireId}"), r.Length, r.BlockSize, true, r.Kind.ToString(), (int)r.WireId)).ToArray();
    });
    public IReadableBlockDevice OpenBlockDevice(BlockDeviceId id, BlockDeviceOpenOptions? options = null) => Execute<IReadableBlockDevice>(() =>
    {
        Ready();
        var region = _storage!.Regions.SingleOrDefault(r => id.Value == $"mtk:{r.Kind}:{r.WireId}") ?? throw new MtkCapabilityException("block device");
        return new MtkBlockDevice(this, region, Generation, options?.Writable == true);
    });
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
