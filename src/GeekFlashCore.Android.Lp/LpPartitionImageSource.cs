using System.Buffers;
using System.Buffers.Binary;
using GeekFlashCore.ImageFormats.Abstractions;
using GeekFlashCore.Android.Sparse;
using GeekFlashCore.Android.Sparse.BlockDevice;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

public sealed class LpPartitionImageSource : IDisposable
{
    private const int SpoolBufferSize = 256 * 1024;

    private readonly IReadableBlockDevice? _device;
    private readonly bool _ownsDevice;
    private readonly SparseDocument? _sparseDocument;
    private readonly SemaphoreSlim _validationGate = new(1, 1);
    private Stream? _sequentialRaw;
    private readonly string? _temporaryPath;
    private int _checksumValidated;
    private int _disposed;

    private LpPartitionImageSource(
        IReadableBlockDevice? device,
        bool ownsDevice,
        SparseDocument? sparseDocument,
        Stream? sequentialRaw,
        long logicalLength,
        string? temporaryPath)
    {
        _device = device;
        _ownsDevice = ownsDevice;
        _sparseDocument = sparseDocument;
        _sequentialRaw = sequentialRaw;
        LogicalLength = logicalLength;
        _temporaryPath = temporaryPath;
    }

    public long LogicalLength { get; }
    public bool IsSparse => _sparseDocument is not null;
    public bool CanReplay => _device is not null;

    public static LpPartitionImageSource FromBlockDevice(
        IReadableBlockDevice source,
        DeviceOwnership ownership,
        ImageReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateOwnership(ownership);
        limits ??= ImageReadLimits.Default;
        limits.Validate();

        try
        {
            bool sparseInput = IsSparseSource(source);
            if (!sparseInput)
            {
                return new LpPartitionImageSource(
                    source,
                    ownership == DeviceOwnership.Transfer,
                    sparseDocument: null,
                    sequentialRaw: null,
                    source.Length,
                    temporaryPath: null);
            }

            SparseDocument sparse = SparseImageParser.Open(source, DeviceOwnership.Borrow);
            return new LpPartitionImageSource(
                source,
                ownership == DeviceOwnership.Transfer,
                sparse,
                sequentialRaw: null,
                sparse.ExpandedLength,
                temporaryPath: null);
        }
        catch
        {
            if (ownership == DeviceOwnership.Transfer)
                source.Dispose();
            throw;
        }
    }

    public static LpPartitionImageSource FromStream(
        Stream source,
        long? rawLength,
        DeviceOwnership ownership,
        ImageReadLimits? limits = null) =>
        FromStreamAsync(source, rawLength, ownership, limits)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public static async ValueTask<LpPartitionImageSource> FromStreamAsync(
        Stream source,
        long? rawLength,
        DeviceOwnership ownership,
        ImageReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException(null, nameof(source));
        }
        if (rawLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawLength));
        }
        ValidateOwnership(ownership);
        limits ??= ImageReadLimits.Default;
        limits.Validate();

        if (source.CanSeek)
        {
            SeekableStreamBlockDevice? device = null;
            try
            {
                bool sparse = SparseImageParser.IsSparse(source);
                long available = checked(source.Length - source.Position);
                if (available < 0 || (!sparse && rawLength is long declaredLength &&
                    declaredLength > available))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(rawLength),
                        Resources.RawLengthExceedsSource);
                }
                long length = sparse ? available : rawLength ?? available;
                device = new SeekableStreamBlockDevice(source, length, ownership);
                return FromOwnedDevice(device, sparse, limits, temporaryPath: null);
            }
            catch
            {
                if (device is not null)
                {
                    device.Dispose();
                }
                else if (ownership == DeviceOwnership.Transfer)
                {
                    source.Dispose();
                }
                throw;
            }
        }

        byte[] prefix;
        try
        {
            prefix = await ReadPrefixAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (ownership == DeviceOwnership.Transfer)
            {
                source.Dispose();
            }
            throw;
        }
        bool sparseInput = prefix.Length == sizeof(uint) &&
            BinaryPrimitives.ReadUInt32LittleEndian(prefix) == 0xed26ff3a;
        if (!sparseInput)
        {
            if (rawLength is null)
            {
                if (ownership == DeviceOwnership.Transfer)
                    source.Dispose();
                throw new ArgumentException(null, nameof(rawLength));
            }

            return new LpPartitionImageSource(
                device: null,
                ownsDevice: false,
                sparseDocument: null,
                sequentialRaw: new PrefixStream(
                    prefix,
                    source,
                    ownership == DeviceOwnership.Transfer),
                rawLength.Value,
                temporaryPath: null);
        }

        string? temporaryPath = null;
        try
        {
            temporaryPath = await SpoolStreamAsync(source, prefix, cancellationToken)
                .ConfigureAwait(false);
            var device = new FileBlockDevice(temporaryPath);
            try
            {
                return FromOwnedDevice(device, sparse: true, limits, temporaryPath);
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }
        catch
        {
            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
            throw;
        }
        finally
        {
            if (ownership == DeviceOwnership.Transfer)
                source.Dispose();
        }
    }

    internal long GetAlignedLength(uint logicalBlockSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalBlockSize, 1u);
        if (LogicalLength == 0)
        {
            return 0;
        }

        long blockSize = logicalBlockSize;
        return checked(((LogicalLength + blockSize - 1) / blockSize) * blockSize);
    }

    internal async ValueTask ValidateAsync(
        int bufferSize,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sparseDocument is null || Volatile.Read(ref _checksumValidated) != 0)
        {
            return;
        }

        await _validationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_checksumValidated != 0)
            {
                return;
            }

            _ = bufferSize;
            _sparseDocument.VerifyChecksum(cancellationToken: cancellationToken);
            Volatile.Write(ref _checksumValidated, 1);
        }
        finally
        {
            _validationGate.Release();
        }
    }

    internal Stream OpenExpandedStream()
    {
        ThrowIfDisposed();
        if (_sparseDocument is not null)
        {
            SparseExpandedBlockDevice expanded = _sparseDocument.CreateExpandedBlockDevice();
            return new BlockDeviceStream(expanded, DeviceOwnership.Transfer);
        }

        if (_device is not null)
        {
            return new BlockDeviceStream(_device, DeviceOwnership.Borrow);
        }

        return Interlocked.Exchange(ref _sequentialRaw, null) ??
            throw new InvalidOperationException(Resources.ImageSourceConsumed);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        LpDisposal.TryDispose(_sequentialRaw, ref failure);
        _sequentialRaw = null;
        LpDisposal.TryDispose(_sparseDocument, ref failure);
        if (_ownsDevice)
        {
            LpDisposal.TryDispose(_device, ref failure);
        }
        LpDisposal.TryDispose(_validationGate, ref failure);
        if (_temporaryPath is not null)
        {
            TryDeleteTemporaryFile(_temporaryPath);
        }
        GC.SuppressFinalize(this);
        LpDisposal.ThrowIfFailed(failure);
    }

    private static LpPartitionImageSource FromOwnedDevice(
        IReadableBlockDevice device,
        bool sparse,
        ImageReadLimits limits,
        string? temporaryPath)
    {
        if (!sparse)
        {
            return new LpPartitionImageSource(
                device,
                ownsDevice: true,
                sparseDocument: null,
                sequentialRaw: null,
                device.Length,
                temporaryPath);
        }

        SparseDocument document = SparseImageParser.Open(device, DeviceOwnership.Borrow);
        return new LpPartitionImageSource(
            device,
            ownsDevice: true,
            document,
            sequentialRaw: null,
            document.ExpandedLength,
            temporaryPath);
    }

    private static async ValueTask<string> SpoolStreamAsync(
        Stream source,
        ReadOnlyMemory<byte> prefix,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"geekflash-lp-{Guid.NewGuid():N}.sparse");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(SpoolBufferSize);
        try
        {
            await using var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                SpoolBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!prefix.IsEmpty)
            {
                await destination.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            }
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await source
                    .ReadAsync(buffer.AsMemory(0, SpoolBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if ((uint)read > SpoolBufferSize)
                {
                    throw new IOException(
                        Resources.InvalidStreamReadResult);
                }
                if (read == 0)
                {
                    break;
                }
                total = checked(total + read);
                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            TryDeleteTemporaryFile(path);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static bool IsSparseSource(IReadableBlockDevice source)
    {
        using var stream = new BlockDeviceStream(source, DeviceOwnership.Borrow);
        return SparseImageParser.IsSparse(stream);
    }

    private static async ValueTask<byte[]> ReadPrefixAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(uint)];
        int read = 0;
        while (read < prefix.Length)
        {
            int count = await source
                .ReadAsync(prefix.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);
            if ((uint)count > (uint)(prefix.Length - read))
                throw new IOException(Resources.InvalidStreamReadResult);
            if (count == 0)
                break;
            read += count;
        }

        return read == prefix.Length ? prefix : prefix.AsSpan(0, read).ToArray();
    }

    private sealed class PrefixStream(
        byte[] prefix,
        Stream source,
        bool ownsSource) : Stream
    {
        private int _prefixOffset;
        private bool _disposed;

        public override bool CanRead => !_disposed && source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
                throw new ArgumentException(null, nameof(offset));
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int written = ReadPrefix(buffer);
            if (written == buffer.Length)
                return written;
            return written + source.Read(buffer[written..]);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int written = ReadPrefix(buffer.Span);
            if (written == buffer.Length)
                return written;
            return written + await source
                .ReadAsync(buffer[written..], cancellationToken)
                .ConfigureAwait(false);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (disposing && ownsSource)
                source.Dispose();
            base.Dispose(disposing);
        }

        private int ReadPrefix(Span<byte> destination)
        {
            int remaining = prefix.Length - _prefixOffset;
            if (remaining <= 0 || destination.IsEmpty)
                return 0;
            int count = Math.Min(remaining, destination.Length);
            prefix.AsSpan(_prefixOffset, count).CopyTo(destination);
            _prefixOffset += count;
            return count;
        }
    }

    private static void ValidateOwnership(DeviceOwnership ownership)
    {
        if (!Enum.IsDefined(ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }
    }

    private static bool TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
