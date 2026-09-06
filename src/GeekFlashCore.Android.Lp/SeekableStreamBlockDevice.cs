using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal sealed class SeekableStreamBlockDevice : IReadableBlockDevice
{
    private readonly Stream _source;
    private readonly object _sync = new();
    private readonly long _origin;
    private readonly bool _ownsSource;
    private bool _disposed;

    internal SeekableStreamBlockDevice(
        Stream source,
        long length,
        DeviceOwnership ownership,
        int logicalBlockSize = 512)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(null, nameof(source));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalBlockSize, 1);
        if (!Enum.IsDefined(ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }

        _origin = source.Position;
        if (_origin > source.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _source = source;
        _ownsSource = ownership == DeviceOwnership.Transfer;
        Length = length;
        LogicalBlockSize = logicalBlockSize;
        Id = new BlockDeviceId($"stream:{Guid.NewGuid():N}");
    }

    public BlockDeviceId Id { get; }
    public long Length { get; }
    public int LogicalBlockSize { get; }

    public int ReadAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || destination.IsEmpty)
        {
            return 0;
        }

        int length = (int)Math.Min(destination.Length, Length - offset);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long previous = _source.Position;
            try
            {
                _source.Position = checked(_origin + offset);
                return _source.Read(destination[..length]);
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
            {
                return;
            }

            _disposed = true;
            if (_ownsSource)
            {
                _source.Dispose();
            }
        }
        GC.SuppressFinalize(this);
    }
}
