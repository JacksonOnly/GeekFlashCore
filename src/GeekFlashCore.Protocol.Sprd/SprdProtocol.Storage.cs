using GeekFlashCore.Android.Sparse;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Sprd.Internals;
using Serilog;

namespace GeekFlashCore.Protocol.Sprd;

public sealed partial class SprdProtocol
{
    /// <inheritdoc />
    public IReadOnlyList<SprdPartition> GetSprdPartitions(CancellationToken cancellationToken = default) =>
        Run(GetPartitionsCore, cancellationToken);
    private IReadOnlyList<SprdPartition> GetPartitionsCore()
    {
        Ready();
        if (_partitions is not null) return _partitions;
        if (_options.PartitionTableSizeUnitBytes is null) throw new InvalidOperationException(Strings.PartitionUnitsRequired);
        var response = _wire.Expect(SprdCommand.ReadPartition, expected: SprdCommand.PartitionTable);
        return _partitions = SprdMetadata.Partitions(response.Data.Span, _options);
    }
    private SprdPartition Find(string name)
    {
        SprdProtocolOptions.ValidatePartitionName(name);
        return GetPartitionsCore().SingleOrDefault(x => x.Name == name) ??
            throw new ArgumentException(Strings.FormatPartitionMissing(name), nameof(name));
    }
    private static void Writable(string name)
    {
        // NV uses checksummed headers and mirrored-device policy, not ordinary image semantics.
        if (name.Contains("nv", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException(Strings.NvUnsupported);
    }
    private void Range(SprdPartition partition, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > partition.Length || length > partition.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (_options.PartitionLengthEncoding == SprdPartitionLengthEncoding.UInt32 &&
            (ulong)offset + (ulong)length > uint.MaxValue) throw new NotSupportedException(Strings.WidthUnsupported);
    }
    /// <inheritdoc />
    public void ReadPartition(string name, long offset, long length, Stream output,
        IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException(Strings.InvalidSource, nameof(output));
        Run(() => { var partition = Find(name); Range(partition, offset, length); ReadCore(partition, offset, length, output, progress); return 0; }, cancellationToken);
    }
    private void ReadCore(SprdPartition partition, long offset, long length, Stream output, IProgress<ProgressRecord>? progress)
    {
        if (length == 0) return;
        Log.ForContext<SprdProtocol>().Information(Strings.Transfer, "read", partition.Name, offset, length);
        // READ_START declares the containing range, including an explicit nonzero read offset.
        _wire.Expect(SprdCommand.ReadStart, SprdMetadata.Selector(partition.Name, checked(offset + length), _options.PartitionLengthEncoding));
        Span<byte> request = stackalloc byte[12];
        bool wide = _options.PartitionLengthEncoding != SprdPartitionLengthEncoding.UInt32;
        long done = 0;
        while (done < length)
        {
            _wire.Check(); int count = (int)Math.Min(_options.TransferBlockSize, length - done);
            BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)count);
            if (wide) BinaryPrimitives.WriteUInt64LittleEndian(request[4..], checked((ulong)(offset + done)));
            else BinaryPrimitives.WriteUInt32LittleEndian(request[4..], checked((uint)(offset + done)));
            var response = _wire.Expect(SprdCommand.ReadMidst, request[..(wide ? 12 : 8)], SprdCommand.ReadFlash);
            if (response.Data.Length != count) throw new SprdProtocolException(SprdCommand.ReadMidst, response.Type);
            output.Write(response.Data.Span); done += count;
            Report(progress, length, done, partition.Name);
        }
        _wire.Expect(SprdCommand.ReadEnd);
        Report(progress, length, length, partition.Name, completed: true);
    }
    /// <inheritdoc />
    public long WritePartition(string name, IDataSource source, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); SprdProtocolOptions.ValidatePartitionName(name); Writable(name);
        return Run(() =>
        {
            Ready(); long length = source.Length;
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(source));
            using Stream stream = source.OpenStream(); ValidateStream(stream, length);
            // Preserve the prefix even for nonseekable Raw sources; Sparse requires a seekable source.
            byte[] prefix = new byte[(int)Math.Min(4, length)]; ReadExactly(stream, prefix);
            bool sparse = prefix.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(prefix) == 0xed26ff3a;
            if (sparse)
            {
                if (!stream.CanSeek) throw new NotSupportedException(Strings.SparseSeekRequired);
                stream.Position -= prefix.Length;
                using var device = new StreamBlockDevice(stream, length, DeviceOwnership.Borrow, logicalBlockSize: 1);
                using var checkedDevice = new CheckedDevice(device, _wire);
                // Cap the existing parser's per-chunk metadata allocation before it sees untrusted counts.
                using (var headerStream = new BlockDeviceStream(checkedDevice, DeviceOwnership.Borrow))
                    SparseSequentialReader.ReadHeader(headerStream, maximumChunks: 262144, cancellationToken: _wire.Token);
                using var document = SparseImageParser.Open(checkedDevice, DeviceOwnership.Borrow);
                var partition = Find(name); Range(partition, 0, document.ExpandedLength);
                _wire.Check(); document.VerifyChecksum(progress: new CheckProgress(_wire), cancellationToken: _wire.Token); _wire.Check();
                using var expanded = document.OpenExpandedStream();
                WriteCore(partition, expanded, document.ExpandedLength, progress);
                return document.ExpandedLength;
            }
            else
            {
                if (_options.PadOddPayloads && length % 2 != 0) throw new ArgumentException(Strings.InvalidSource, nameof(source));
                var partition = Find(name); Range(partition, 0, length);
                WriteCore(partition, stream, length, progress, prefix); return length;
            }
        }, cancellationToken);
    }
    private void WriteCore(SprdPartition partition, Stream stream, long length, IProgress<ProgressRecord>? progress, ReadOnlySpan<byte> prefix = default)
    {
        Log.ForContext<SprdProtocol>().Information(Strings.Transfer, "write", partition.Name, 0, length);
        _wire.Expect(SprdCommand.Start, SprdMetadata.Selector(partition.Name, length, _options.PartitionLengthEncoding));
        SendStream(stream, length, _options.TransferBlockSize, partition.Name, progress, prefix);
        _wire.Expect(SprdCommand.End);
        Report(progress, length, length, partition.Name, completed: true);
    }
    /// <inheritdoc />
    public void ErasePartition(string name, CancellationToken cancellationToken = default)
    {
        SprdProtocolOptions.ValidatePartitionName(name); Writable(name);
        Run(() => { var partition = Find(name);
            Log.ForContext<SprdProtocol>().Information(Strings.Transfer, "erase", partition.Name, 0, partition.Length);
            _wire.Expect(SprdCommand.Erase,
            SprdMetadata.Selector(partition.Name, 0, SprdPartitionLengthEncoding.UInt32)); return 0; }, cancellationToken);
    }
    /// <inheritdoc />
    public void Reboot(ProtocolRebootMode mode, CancellationToken cancellationToken = default)
    {
        ushort command = mode switch { ProtocolRebootMode.System => SprdCommand.Reset, ProtocolRebootMode.PowerOff => SprdCommand.PowerOff,
            _ => throw new NotSupportedException(Strings.RebootUnsupported) };
        Run(() => { Ready(); _wire.Expect(command); DisconnectCore(); return 0; }, cancellationToken);
    }
    private static string TargetName(StorageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target is not PartitionTarget partition || partition.PhysicalPartitionNumber is not (null or 0))
            throw new NotSupportedException(Strings.UnsupportedTarget);
        SprdProtocolOptions.ValidatePartitionName(partition.Name); return partition.Name;
    }
    /// <inheritdoc />
    public Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PartitionInfo>>(GetSprdPartitions(ct).Select(p => new PartitionInfo(p.Name, null, null, p.Length,
            new Dictionary<string, string> { ["PhysicalPartitionNumber"] = "0", ["Addressing"] = "NamedPartition" })).ToArray());
    /// <inheritdoc />
    public Task<long> WriteAsync(WriteSource source, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    { ArgumentNullException.ThrowIfNull(source); return Task.FromResult(WritePartition(TargetName(source.Target), source.Source, progress, ct)); }
    /// <inheritdoc />
    public Task<ReadDestination> ReadAsync(ReadDestination destination, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string name = TargetName(destination.Target);
        if (destination.OutputStream is null || !destination.OutputStream.CanWrite) throw new ArgumentException(Strings.InvalidSource, nameof(destination));
        Run(() => { var partition = Find(name); Range(partition, 0, partition.Length);
            ReadCore(partition, 0, partition.Length, destination.OutputStream, progress); return 0; }, ct);
        return Task.FromResult(destination);
    }
    /// <inheritdoc />
    public Task<bool> EraseAsync(StorageTarget target, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    { ErasePartition(TargetName(target), ct); return Task.FromResult(true); }
    /// <inheritdoc />
    public Task<bool> RebootAsync(ProtocolRebootMode mode, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    { Reboot(mode, ct); return Task.FromResult(true); }
    /// <inheritdoc />
    public IReadableBlockDevice OpenPartition(string name, CancellationToken cancellationToken = default) =>
        Run<IReadableBlockDevice>(() => new PartitionView(this, Find(name), Generation), cancellationToken);
    private sealed class PartitionView(SprdProtocol owner, SprdPartition partition, long generation) : IReadableBlockDevice
    {
        private int _disposed;
        public BlockDeviceId Id { get; } = new($"sprd:{Guid.NewGuid():N}:{generation}:{partition.Name}");
        public long Length => partition.Length;
        public int LogicalBlockSize => 1;
        public int ReadAt(long offset, Span<byte> destination)
        {
            // Span cannot be captured by Run's delegate; acquire the same gate directly.
            using var gate = owner.Enter(default);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            owner.Ready();
            if (owner.Generation != generation) throw new InvalidOperationException(Strings.Unavailable);
            if (offset < 0 || offset > Length) throw new ArgumentOutOfRangeException(nameof(offset));
            int count = (int)Math.Min(destination.Length, Length - offset);
            owner.Range(partition, offset, count);
            if (count == 0) return 0;
            owner._wire.Begin(default, owner._options.OperationTimeoutMilliseconds);
            try
            {
                // Stream directly into the caller's span a chunk at a time, with one read transaction.
                owner._wire.Expect(SprdCommand.ReadStart, SprdMetadata.Selector(partition.Name, checked(offset + count), owner._options.PartitionLengthEncoding));
                Span<byte> request = stackalloc byte[12]; bool wide = owner._options.PartitionLengthEncoding != SprdPartitionLengthEncoding.UInt32;
                int done = 0;
                while (done < count)
                {
                    int n = Math.Min(count - done, owner._options.TransferBlockSize);
                    BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)n);
                    if (wide) BinaryPrimitives.WriteUInt64LittleEndian(request[4..], checked((ulong)(offset + done)));
                    else BinaryPrimitives.WriteUInt32LittleEndian(request[4..], checked((uint)(offset + done)));
                    var response = owner._wire.Expect(SprdCommand.ReadMidst, request[..(wide ? 12 : 8)], SprdCommand.ReadFlash);
                    if (response.Data.Length != n) throw new SprdProtocolException(SprdCommand.ReadMidst, response.Type);
                    response.Data.Span.CopyTo(destination.Slice(done, n)); done += n;
                }
                owner._wire.Expect(SprdCommand.ReadEnd); owner._wire.Check(); return count;
            }
            catch { owner.Fault(); throw; }
        }
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
    private sealed class CheckedDevice(IReadableBlockDevice source, SprdWire wire) : IReadableBlockDevice
    {
        public BlockDeviceId Id => source.Id;
        public long Length => source.Length;
        public int LogicalBlockSize => source.LogicalBlockSize;
        public int ReadAt(long offset, Span<byte> destination)
        { wire.Check(); int count = source.ReadAt(offset, destination); wire.Check(); return count; }
        public void Dispose() { } // Underlying device is borrowed.
    }
    private sealed class CheckProgress(SprdWire wire) : IProgress<BlockCopyProgress>
    { public void Report(BlockCopyProgress value) => wire.Check(); }
}
