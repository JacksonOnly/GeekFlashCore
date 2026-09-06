namespace GeekFlashCore.BlockDevice;

/// <summary>Exposes a fixed window of a readable, seekable stream as a block device.</summary>
public sealed class StreamBlockDevice : IReadableBlockDevice
{
    private readonly Stream _source;
    private readonly object _sync = new();
    private readonly long _origin;
    private readonly bool _ownsSource;
    private bool _disposed;

    public StreamBlockDevice(
        Stream source,
        long length,
        DeviceOwnership ownership,
        int logicalBlockSize = 512,
        BlockDeviceId? id = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException(Strings.StreamMustBeReadableAndSeekable, nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalBlockSize, 1);
        if (!Enum.IsDefined(ownership))
            throw new ArgumentOutOfRangeException(nameof(ownership));

        _origin = source.Position;
        if (_origin > source.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length));

        _source = source;
        _ownsSource = ownership == DeviceOwnership.Transfer;
        Length = length;
        LogicalBlockSize = logicalBlockSize;
        Id = id ?? new BlockDeviceId($"stream:{Guid.NewGuid():N}");
    }

    public BlockDeviceId Id { get; }
    public long Length { get; }
    public int LogicalBlockSize { get; }

    public int ReadAt(long offset, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int length = BlockDeviceIO.GetReadLength(Length, offset, destination.Length);
            if (length == 0)
                return 0;

            long previous = _source.Position;
            try
            {
                _source.Position = checked(_origin + offset);
                return BlockDeviceIO.ValidateReadResult(
                    _source.Read(destination[..length]),
                    length);
            }
            finally
            {
                _source.Position = previous;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_ownsSource)
                _source.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
