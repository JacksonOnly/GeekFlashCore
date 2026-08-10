namespace GeekFlashCore.IO.BlockDevice;

public sealed class ReadAheadBlockDevice : IReadableBlockDevice
{
    private readonly IReadableBlockDevice _source;
    private readonly bool _ownsSource;
    private readonly byte[] _window;
    private readonly object _gate = new();
    private long _windowOffset = -1;
    private int _windowLength;
    private bool _disposed;

    public ReadAheadBlockDevice(IReadableBlockDevice source, int windowSize, bool leaveOpen = false)
        : this(
            source,
            windowSize,
            BlockDeviceLimits.Default,
            leaveOpen ? DeviceOwnership.Borrow : DeviceOwnership.Transfer)
    {
    }

    public ReadAheadBlockDevice(
        IReadableBlockDevice source,
        int windowSize,
        BlockDeviceLimits limits,
        DeviceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        if (!Enum.IsDefined(ownership)) throw new ArgumentOutOfRangeException(nameof(ownership));

        int alignedWindowSize = limits.ValidateReadAheadSize(windowSize, source.LogicalBlockSize);
        _window = new byte[alignedWindowSize];
        _source = source;
        _ownsSource = ownership == DeviceOwnership.Transfer;
    }

    public BlockDeviceId Id => _source.Id;
    public long Length => _source.Length;
    public int LogicalBlockSize => _source.LogicalBlockSize;

    public int ReadAt(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        int totalLength = BlockDeviceIO.GetReadLength(this, offset, destination.Length);
        if (totalLength == 0) return 0;

        if (totalLength >= _window.Length)
        {
            int read = _source.ReadAt(offset, destination[..totalLength]);
            return BlockDeviceIO.ValidateReadResult(read, totalLength);
        }

        lock (_gate)
        {
            int copied = 0;
            while (copied < totalLength)
            {
                long currentOffset = checked(offset + copied);
                EnsureWindow(currentOffset);
                int windowIndex = checked((int)(currentOffset - _windowOffset));
                int partLength = Math.Min(totalLength - copied, _windowLength - windowIndex);
                if (partLength <= 0)
                {
                    throw new EndOfStreamException(Strings.ReadAheadMadeNoProgress);
                }

                _window.AsSpan(windowIndex, partLength).CopyTo(destination[copied..]);
                copied += partLength;
            }

            return copied;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsSource) _source.Dispose();
        GC.SuppressFinalize(this);
    }

    private void EnsureWindow(long offset)
    {
        if (Contains(offset)) return;

        long windowOffset = AlignDown(offset, LogicalBlockSize);
        int length = (int)Math.Min(_window.Length, Length - windowOffset);
        _windowOffset = windowOffset;
        _windowLength = 0;
        BlockDeviceIO.ReadExactlyAt(_source, windowOffset, _window.AsSpan(0, length));
        _windowLength = length;
    }

    private bool Contains(long offset) =>
        _windowOffset >= 0 &&
        offset >= _windowOffset &&
        offset - _windowOffset < _windowLength;

    private static long AlignDown(long value, int alignment) => value - value % alignment;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
