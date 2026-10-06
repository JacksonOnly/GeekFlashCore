using System.Buffers;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed class PartitionDeviceResolver(IBlockDeviceProvider provider, IReadOnlyList<PartitionInfo> partitions,
    PartitionInfo primary, Action? validateWrite = null) : ILpBlockDeviceResolver, ILpWritableBlockDeviceResolver
{
    internal IReadableBlockDevice Open(PartitionInfo partition, bool writable = false)
    {
        if (writable) validateWrite?.Invoke();
        uint lun = StorageCommands.PartitionLun(partition);
        var descriptor = provider.GetBlockDevices().SingleOrDefault(x => x.PhysicalPartitionNumber == lun) ??
            throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
        if (writable && !descriptor.CanWrite) throw new NotSupportedException(Strings.Cli_LpNotWritable);
        if (partition.Offset is not { } offset || partition.Length is not { } length || offset < 0 || length < 0 ||
            offset > descriptor.Length - length)
            throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
        if (writable && (descriptor.LogicalBlockSize is < 1 or > 4 * 1024 * 1024 ||
            offset % descriptor.LogicalBlockSize != 0 || length % descriptor.LogicalBlockSize != 0))
            throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
        IReadableBlockDevice source = provider.OpenBlockDevice(descriptor.Id, new BlockDeviceOpenOptions { Writable = writable });
        try
        {
            if (source.Length != descriptor.Length || source.LogicalBlockSize != descriptor.LogicalBlockSize)
                throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
            var id = new BlockDeviceId($"{descriptor.Id}/{partition.Name}");
            return writable ? new WritablePartitionSlice(source, id, offset, length) :
                new SliceBlockDevice(source, id, offset, length, DeviceOwnership.Transfer);
        }
        catch { source.Dispose(); throw; }
    }

    private PartitionInfo Find(LpBlockDevice blockDevice)
    {
        var matches = primary.Name == blockDevice.PartitionName ? new[] { primary } :
            partitions.Where(x => x.Name == blockDevice.PartitionName).ToArray();
        if (matches.Length != 1) throw new ArgumentException(Strings.FormatCli_PartitionNotUnique(blockDevice.PartitionName));
        return matches[0];
    }

    public ValueTask<IReadableBlockDeviceLease> ResolveAsync(LpBlockDevice blockDevice, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadableBlockDeviceLease>(new BlockDeviceLease(Open(Find(blockDevice)), DeviceOwnership.Transfer));
    }

    ValueTask<IWritableBlockDeviceLease> ILpWritableBlockDeviceResolver.ResolveAsync(LpBlockDevice blockDevice,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IWritableBlockDeviceLease>(new WritableBlockDeviceLease(
            (IWritableBlockDevice)Open(Find(blockDevice), writable: true), DeviceOwnership.Transfer));
    }

    private sealed class WritablePartitionSlice : IWritableBlockDevice, IBlockDeviceFlushDurability
    {
        private readonly IWritableBlockDevice _source;
        private readonly long _offset;
        private bool _disposed;

        internal WritablePartitionSlice(IReadableBlockDevice source, BlockDeviceId id, long offset, long length)
        {
            _source = source as IWritableBlockDevice ?? throw new NotSupportedException(Strings.Cli_LpNotWritable);
            Id = id; _offset = offset; Length = length;
        }

        public BlockDeviceId Id { get; }
        public long Length { get; }
        public int LogicalBlockSize => _source.LogicalBlockSize;
        public BlockDeviceFlushDurability FlushDurability => (_source as IBlockDeviceFlushDurability)?.FlushDurability ??
            BlockDeviceFlushDurability.WriteAccepted;
        public int ReadAt(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int length = BlockDeviceIO.GetReadLength(this, offset, destination.Length);
            return length == 0 ? 0 : BlockDeviceIO.ValidateReadResult(_source.ReadAt(checked(_offset + offset), destination[..length]), length);
        }
        public void WriteAt(long offset, ReadOnlySpan<byte> source)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < 0 || offset > Length - source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (source.IsEmpty) return;
            int sector = LogicalBlockSize;
            long physical = checked(_offset + offset);
            if (physical % sector == 0 && source.Length % sector == 0) { _source.WriteAt(physical, source); return; }
            // LP plans are byte ranges (for example a five-byte Raw image followed by zero fill).
            // Transport writes remain complete sectors; preserve bytes outside each partial range.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(sector);
            try
            {
                int head = checked((int)(physical % sector));
                if (head != 0)
                {
                    int count = Math.Min(sector - head, source.Length);
                    WritePartial(physical - head, head, source[..count], buffer.AsSpan(0, sector));
                    physical = checked(physical + count); source = source[count..];
                }
                int aligned = source.Length / sector * sector;
                if (aligned > 0)
                {
                    _source.WriteAt(physical, source[..aligned]);
                    physical = checked(physical + aligned); source = source[aligned..];
                }
                if (!source.IsEmpty) WritePartial(physical, 0, source, buffer.AsSpan(0, sector));
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        private void WritePartial(long physical, int offset, ReadOnlySpan<byte> source, Span<byte> sector)
        {
            BlockDeviceIO.ReadExactlyAt(_source, physical, sector);
            source.CopyTo(sector[offset..]);
            _source.WriteAt(physical, sector);
        }
        public void Flush() { ObjectDisposedException.ThrowIf(_disposed, this); _source.Flush(); }
        public void Dispose() { if (_disposed) return; _disposed = true; _source.Dispose(); }
    }
}
