namespace GeekFlashCore.IO.BlockDevice;

public sealed class BlockDeviceStream : Stream
{
    private readonly IReadableBlockDevice _device;
    private readonly IWritableBlockDevice? _writableDevice;
    private readonly bool _ownsDevice;
    private long _position;
    private bool _disposed;

    public BlockDeviceStream(IReadableBlockDevice device, bool leaveOpen = false)
        : this(device, leaveOpen ? DeviceOwnership.Borrow : DeviceOwnership.Transfer)
    {
    }

    public BlockDeviceStream(IReadableBlockDevice device, DeviceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Enum.IsDefined(ownership)) throw new ArgumentOutOfRangeException(nameof(ownership));
        _device = device;
        _writableDevice = device as IWritableBlockDevice;
        _ownsDevice = ownership == DeviceOwnership.Transfer;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed && _writableDevice is not null;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _device.Length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set
        {
            ThrowIfDisposed();
            ValidatePosition(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        int allowedLength = BlockDeviceIO.GetReadLength(_device, _position, buffer.Length);
        if (allowedLength == 0) return 0;
        int read = _device.ReadAt(_position, buffer[..allowedLength]);
        read = BlockDeviceIO.ValidateReadResult(read, allowedLength);
        _position = checked(_position + read);
        return read;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_device.Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        ValidatePosition(position);
        _position = position;
        return position;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        IWritableBlockDevice writable = GetWritableDevice();
        ValidateWriteLength(buffer.Length);
        writable.WriteAt(_position, buffer);
        _position = checked(_position + buffer.Length);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        _writableDevice?.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    public override void SetLength(long value) => throw new NotSupportedException(Strings.BlockDeviceLengthFixed);

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing && _ownsDevice) _device.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private IWritableBlockDevice GetWritableDevice() =>
        _writableDevice ?? throw new NotSupportedException(Strings.BlockDeviceReadOnly);

    private void ValidateWriteLength(int length)
    {
        if (_position > _device.Length - length)
            throw new BlockDeviceException(Strings.WriteExceedsBlockDeviceBoundary);
    }

    private void ValidatePosition(long value)
    {
        if (value < 0 || value > _device.Length)
            throw new BlockDeviceException(Strings.PositionOutsideBlockDeviceBoundary);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
