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
        if (_options.PartitionTableSource == SprdPartitionTableSource.UserPartitionGpt)
            return _partitions = GetGptPartitionsCore();
        if (_options.PartitionTableSource == SprdPartitionTableSource.Native || _nativeRecords is not null)
            return _partitions = GetNativePartitionsCore();
        return _partitions = GetGptPartitionsCore(detectSource: true);
    }
    private IReadOnlyList<SprdPartition> GetNativePartitionsCore()
    {
        if (_options.PartitionTableSource == SprdPartitionTableSource.Native && _options.PartitionTableSizeUnitBytes is null)
            throw new InvalidOperationException(Strings.PartitionUnitsRequired);
        if (_nativeRecords is null)
        {
            var response = _wire.Expect(SprdCommand.ReadPartition, expected: SprdCommand.PartitionTable);
            _nativeRecords = SprdMetadata.PartitionRecords(response.Data.Span, _options);
        }
        _wire.Check();
        if (_options.PartitionTableSizeUnitBytes is not long unit)
        {
            SetPartitionSource(SprdPartitionTableSource.Native);
            _wire.Check();
            // Completed read/cleanup and validated records; this is a host configuration error.
            throw new SprdNativeUnitsRequiredException();
        }
        var result = SprdMetadata.ScalePartitions(_nativeRecords, unit);
        _wire.Check(); SetPartitionSource(SprdPartitionTableSource.Native);
        return result;
    }
    private void SetPartitionSource(SprdPartitionTableSource source, int? sector = null)
    {
        if (_target!.PartitionTableSource == source && _target.GptSectorSize == sector) return;
        _target = _target with { PartitionTableSource = source, GptSectorSize = sector };
        Log.ForContext<SprdProtocol>().Information(Strings.PartitionSourceDetected, source);
    }
    /// <inheritdoc />
    public byte[] ReadChipUid(CancellationToken cancellationToken = default) => Run(() =>
    {
        Ready();
        var response = _wire.Expect(SprdCommand.ReadChipUid, expected: SprdCommand.ChipUid);
        if (response.Data.Length is < 1 or > 256) throw new SprdProtocolException(SprdCommand.ReadChipUid, response.Type);
        return response.Data.ToArray();
    }, cancellationToken);
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
    private void ReadCore(SprdPartition partition, long offset, long length, Stream output, IProgress<ProgressRecord>? progress,
        bool readStarted = false)
    {
        if (length == 0) return;
        Log.ForContext<SprdProtocol>().Information(Strings.Transfer, "read", partition.Name, offset, length);
        // READ_START declares the containing range, including an explicit nonzero read offset.
        if (!readStarted)
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
        if (_options.RawDataMode == SprdRawDataMode.Disabled)
            SendStream(stream, length, _options.TransferBlockSize, partition.Name, progress, prefix);
        else SendRawStream(stream, length, partition.Name, progress, prefix);
        _wire.Expect(SprdCommand.End);
        Report(progress, length, length, partition.Name, completed: true);
    }
    private void SendRawStream(Stream stream, long length, string label, IProgress<ProgressRecord>? progress, ReadOnlySpan<byte> prefix)
    {
        int blockSize = _options.RawDataFlushSizeBytes!.Value;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            if (_options.RawDataMode == SprdRawDataMode.Version2) _wire.Expect(SprdCommand.MidstRawStart2);
            Span<byte> request = stackalloc byte[12]; long sent = 0;
            while (sent < length)
            {
                _wire.Check(); int count = (int)Math.Min(blockSize, length - sent), copied = Math.Min(prefix.Length, count);
                prefix[..copied].CopyTo(buffer); prefix = prefix[copied..];
                ReadExactly(stream, buffer.AsSpan(copied, count - copied));
                if (_options.RawDataMode == SprdRawDataMode.Version1)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(request, checked((ulong)sent));
                    BinaryPrimitives.WriteUInt32LittleEndian(request[8..], (uint)count);
                    _wire.Expect(SprdCommand.MidstRawStart, request);
                }
                _wire.Raw(buffer.AsSpan(0, count)); sent += count;
                Report(progress, length, sent, label);
            }
            _wire.Check(); if (stream.ReadByte() != -1) throw new IOException(Strings.InvalidSource);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
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
