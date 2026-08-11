namespace GeekFlashCore.BlockDevice;

public sealed class SliceBlockDevice : IReadableBlockDevice
{
    private readonly IReadableBlockDevice _source;
    private readonly long _sourceOffset;
    private readonly bool _ownsSource;
    private bool _disposed;

    public SliceBlockDevice(
        IReadableBlockDevice source,
        BlockDeviceId id,
        long sourceOffset,
        long length,
        bool leaveOpen = false)
        : this(
            source,
            id,
            sourceOffset,
            length,
            leaveOpen ? DeviceOwnership.Borrow : DeviceOwnership.Transfer)
    {
    }

    public SliceBlockDevice(
        IReadableBlockDevice source,
        BlockDeviceId id,
        long sourceOffset,
        long length,
        DeviceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (id.IsEmpty) throw new ArgumentException(Strings.BlockDeviceIdRequired, nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!Enum.IsDefined(ownership)) throw new ArgumentOutOfRangeException(nameof(ownership));
        if (sourceOffset > source.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length), Strings.SliceExceedsSource);

        _source = source;
        _sourceOffset = sourceOffset;
        _ownsSource = ownership == DeviceOwnership.Transfer;
        Id = id;
        Length = length;
    }

    public BlockDeviceId Id { get; }
    public long Length { get; }
    public int LogicalBlockSize => _source.LogicalBlockSize;

    public int ReadAt(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        int length = BlockDeviceIO.GetReadLength(this, offset, destination.Length);
        if (length == 0) return 0;
        int read = _source.ReadAt(checked(_sourceOffset + offset), destination[..length]);
        return BlockDeviceIO.ValidateReadResult(read, length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsSource) _source.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
