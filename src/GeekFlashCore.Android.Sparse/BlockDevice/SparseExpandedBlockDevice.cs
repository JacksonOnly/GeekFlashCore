using System.Buffers.Binary;
using GeekFlashCore.Android.Sparse.Types;
using GeekFlashCore.Android.Sparse.Internals;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Sparse.BlockDevice;


public sealed class SparseExpandedBlockDevice : IReadableBlockDevice
{
    private readonly SparseDocument _document;
    private bool _disposed;

    internal SparseExpandedBlockDevice(SparseDocument document, BlockDeviceId id)
    {
        _document = document;
        Id = id;
    }

    public BlockDeviceId Id { get; }
    public long Length => _document.ExpandedLength;
    public int LogicalBlockSize => checked((int)_document.Header.BlockSize);

    public int ReadAt(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        _document.ThrowIfDisposed();
        int totalLength = BlockDeviceIO.GetReadLength(this, offset, destination.Length);
        int written = 0;

        while (written < totalLength)
        {
            long logicalOffset = checked(offset + written);
            SparseChunk chunk = FindChunk(logicalOffset);
            int partLength = (int)Math.Min(
                totalLength - written,
                checked(chunk.OutputOffset + chunk.OutputLength) - logicalOffset);
            Span<byte> part = destination.Slice(written, partLength);
            long offsetInChunk = logicalOffset - chunk.OutputOffset;

            switch (chunk.Type)
            {
                case SparseChunkType.Raw:
                    BlockDeviceIO.ReadExactlyAt(
                        _document.Source,
                        checked(chunk.PayloadOffset + offsetInChunk),
                        part);
                    break;

                case SparseChunkType.Fill:
                    SparsePattern.Fill(part, chunk.FillValue, offsetInChunk);
                    break;

                case SparseChunkType.DontCare:
                    part.Clear();
                    break;

                default:
                    throw new InvalidDataException(Strings.NonDataChunkMapped);
            }

            written += partLength;
        }

        return totalLength;
    }

    public void Dispose()
    {
        _disposed = true;
        // ReSharper disable once GCSuppressFinalizeForTypeWithoutDestructor
        GC.SuppressFinalize(this);
    }

    private SparseChunk FindChunk(long logicalOffset)
    {
        ReadOnlySpan<SparseChunk> chunks = _document.ChunkSpan;
        int low = 0;
        int high = chunks.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            SparseChunk chunk = chunks[middle];
            if (logicalOffset < chunk.OutputOffset)
            {
                high = middle - 1;
            }
            else if (logicalOffset >= chunk.OutputOffset + chunk.OutputLength)
            {
                low = middle + 1;
            }
            else
            {
                return chunk;
            }
        }

        throw new InvalidDataException(Strings.ChunkIndexDoesNotCoverOffset);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
