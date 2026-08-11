using System.Globalization;
using GeekFlashCore.Android.Sparse.BlockDevice;
using GeekFlashCore.Android.Sparse.Internals;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.Android.Sparse.Types;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;
using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.Android.Sparse;

public sealed class SparseDocument : IDisposable
{
    private readonly IReadableBlockDevice _source;
    private readonly SparseChunk[] _chunks;
    private readonly bool _ownsSource;
    private int _checksumStatus;
    private uint _verifiedChecksum;
    private bool _disposed;

    internal SparseDocument(
        IReadableBlockDevice source,
        SparseHeader header,
        SparseChunk[] chunks,
        long parsedPhysicalLength,
        DeviceOwnership ownership)
    {
        _source = source;
        _chunks = chunks;
        _ownsSource = ownership == DeviceOwnership.Transfer;
        Header = header;
        PhysicalLength = source.Length;
        ParsedPhysicalLength = parsedPhysicalLength;
        bool hasChecksum = header.ImageChecksum != 0 ||
                           Array.Exists(chunks, static chunk => chunk.Type == SparseChunkType.Crc32);
        _checksumStatus = (int)(hasChecksum
            ? SparseChecksumStatus.NotVerified
            : SparseChecksumStatus.NotPresent);
        _disposed = false;
    }

    public SparseHeader Header { get; }
    public long PhysicalLength { get; }
    public long ParsedPhysicalLength { get; }
    public long ExpandedLength => Header.RawLength;

    public SparseChecksumStatus ChecksumStatus =>
        (SparseChecksumStatus)Volatile.Read(ref _checksumStatus);

    public ReadOnlyMemory<SparseChunk> Chunks => _chunks;

    public SparseExpandedBlockDevice CreateExpandedBlockDevice(BlockDeviceId? id = null)
    {
        ThrowIfDisposed();
        BlockDeviceId deviceId = id ?? new BlockDeviceId($"sparse-expanded:{_source.Id}");
        return new SparseExpandedBlockDevice(this, deviceId);
    }

    /// <summary>Opens a readable, seekable stream over the expanded image.</summary>
    /// <remarks>
    /// The stream reads through this document. Keep the document alive until the returned stream is disposed.
    /// </remarks>
    public Stream OpenExpandedStream()
    {
        ThrowIfDisposed();
        return new BlockDeviceStream(CreateExpandedBlockDevice(), DeviceOwnership.Transfer);
    }

    public uint VerifyChecksum(
        BudgetedArrayPool? buffers = null,
        IProgress<BlockCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (ChecksumStatus == SparseChecksumStatus.Verified)
            return _verifiedChecksum;

        buffers ??= new BudgetedArrayPool(
            new ByteBudget(BoundedStreamCopier.MaximumBufferSize));
        using PooledBufferLease lease = buffers.Rent(
            BoundedStreamCopier.DefaultBufferSize,
            cancellationToken);
        Span<byte> buffer = lease.Memory.Span;
        uint checksum = 0;
        long completed = 0;
        bool hasChecksum = Header.ImageChecksum != 0;

        for (int index = 0; index < _chunks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SparseChunk chunk = _chunks[index];
            if (chunk.Type == SparseChunkType.Crc32)
            {
                hasChecksum = true;
                if (chunk.ExpectedCrc32 != checksum)
                    throw ChecksumMismatch(index, chunk.PayloadOffset, chunk.ExpectedCrc32, checksum);
                continue;
            }

            long chunkCompleted = 0;
            while (chunkCompleted < chunk.OutputLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = (int)Math.Min(buffer.Length, chunk.OutputLength - chunkCompleted);
                Span<byte> part = buffer[..length];
                switch (chunk.Type)
                {
                    case SparseChunkType.Raw:
                        BlockDeviceIO.ReadExactlyAt(
                            _source,
                            checked(chunk.PayloadOffset + chunkCompleted),
                            part);
                        break;
                    case SparseChunkType.Fill:
                        SparsePattern.Fill(part, chunk.FillValue, chunkCompleted);
                        break;
                    case SparseChunkType.DontCare:
                        part.Clear();
                        break;
                    default:
                        throw new SparseException(Strings.NonDataChunkMapped);
                }

                checksum = Crc32Helper.Append(checksum, part);
                chunkCompleted += length;
                completed += length;
                progress?.Report(new BlockCopyProgress(completed, ExpandedLength));
            }
        }

        if (Header.ImageChecksum != 0 && Header.ImageChecksum != checksum)
            throw ImageChecksumMismatch(Header.ImageChecksum, checksum);

        _verifiedChecksum = checksum;
        if (hasChecksum)
            Volatile.Write(ref _checksumStatus, (int)SparseChecksumStatus.Verified);
        return checksum;
    }

    public async ValueTask ExportExpandedAsync(
        Stream destination,
        IProgress<BlockCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using SparseExpandedBlockDevice expanded = CreateExpandedBlockDevice();
        await BlockDeviceExporter.ExportAsync(expanded, destination, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public SparseImageWritePlan ExportSparse(
        Stream destination,
        SparseImageWriteOptions? options = null,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null)
    {
        ThrowIfDisposed();
        options ??= new SparseImageWriteOptions
        {
            BlockSize = checked((int)Header.BlockSize)
        };
        using SparseExpandedBlockDevice expanded = CreateExpandedBlockDevice();
        return SparseImageWriter.Write(expanded, destination, options, progress, buffers);
    }

    public async ValueTask<SparseImageWritePlan> ExportSparseAsync(
        Stream destination,
        SparseImageWriteOptions? options = null,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        options ??= new SparseImageWriteOptions
        {
            BlockSize = checked((int)Header.BlockSize)
        };
        using SparseExpandedBlockDevice expanded = CreateExpandedBlockDevice();
        return await SparseImageWriter
            .WriteAsync(expanded, destination, options, progress, buffers, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsSource) _source.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    internal IReadableBlockDevice Source
    {
        get
        {
            ThrowIfDisposed();
            return _source;
        }
    }

    internal ReadOnlySpan<SparseChunk> ChunkSpan
    {
        get
        {
            ThrowIfDisposed();
            return _chunks;
        }
    }

    internal void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SparseDocument));
    }

    private SparseException ChecksumMismatch(
        int chunkIndex,
        long payloadOffset,
        uint expected,
        uint actual) =>
        new(
            $"{Strings.FormatImageChecksumMismatch(
                expected.ToString("X8", CultureInfo.InvariantCulture),
                actual.ToString("X8", CultureInfo.InvariantCulture))} "
            + $"blockDeviceId: {_source.Id} "
            + $"deviceRelativeOffset:  {payloadOffset} "
            + $"chunkIndex: {chunkIndex}"
        );


    private SparseException ImageChecksumMismatch(uint expected, uint actual) =>
        new(
            $"{Strings.FormatImageChecksumMismatch(
                expected.ToString("X8", CultureInfo.InvariantCulture),
                actual.ToString("X8", CultureInfo.InvariantCulture))} "
            + $"blockDeviceId: {_source.Id}");
}
