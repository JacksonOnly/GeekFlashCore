using System.Buffers;
namespace GeekFlashCore.BlockDevice;

public sealed class ReadAheadBlockDevice : IReadableBlockDevice
{
    private readonly IReadableBlockDevice _source;
    private readonly bool _ownsSource;
    private readonly ArrayPool<byte> _pool;
    private readonly int _windowSize;
    private readonly object _gate = new();
    private byte[]? _window;
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
        _pool = ArrayPool<byte>.Shared;
        _window = _pool.Rent(alignedWindowSize);
        _windowSize = alignedWindowSize;
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

        if (totalLength >= _windowSize)
        {
            int read = _source.ReadAt(offset, destination[..totalLength]);
            return BlockDeviceIO.ValidateReadResult(read, totalLength);
        }

        lock (_gate)
        {
            byte[] window = GetWindow();
            int copied = 0;
            while (copied < totalLength)
            {
                long currentOffset = checked(offset + copied);
                EnsureWindow(window, currentOffset);
                int windowIndex = checked((int)(currentOffset - _windowOffset));
                int partLength = Math.Min(totalLength - copied, _windowLength - windowIndex);
                if (partLength <= 0)
                {
                    throw new BlockDeviceException(Strings.ReadAheadMadeNoProgress);
                }

                window.AsSpan(windowIndex, partLength).CopyTo(destination[copied..]);
                copied += partLength;
            }

            return copied;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        byte[]? window = Interlocked.Exchange(ref _window, null);
        try
        {
            if (_ownsSource) _source.Dispose();
        }
        finally
        {
            if (window is not null)
            {
                window.AsSpan(0, _windowSize).Clear();
                _pool.Return(window);
            }

            GC.SuppressFinalize(this);
        }
    }

    private void EnsureWindow(byte[] window, long offset)
    {
        if (Contains(offset)) return;

        long windowOffset = AlignDown(offset, LogicalBlockSize);
        int length = (int)Math.Min(_windowSize, Length - windowOffset);
        _windowOffset = windowOffset;
        _windowLength = 0;
        BlockDeviceIO.ReadExactlyAt(_source, windowOffset, window.AsSpan(0, length));
        _windowLength = length;
    }

    private bool Contains(long offset) =>
        _windowOffset >= 0 &&
        offset >= _windowOffset &&
        offset - _windowOffset < _windowLength;

    private static long AlignDown(long value, int alignment) => value - value % alignment;
    private byte[] GetWindow() =>
        Volatile.Read(ref _window) ?? throw new ObjectDisposedException(nameof(ReadAheadBlockDevice));
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
