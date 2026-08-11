using System.Buffers.Binary;
using GeekFlashCore.FileSystem.Erofs.Internals;
using GeekFlashCore.FileSystem.Erofs.Models;
using GeekFlashCore.FileSystem.Erofs.Types;
using GeekFlashCore.IO.BlockDevice;

namespace GeekFlashCore.FileSystem.Erofs;

public sealed class ErofsFileStream : Stream
{
    private const ushort ChunkBlockBitsMask = 0x001F;
    private const ushort ChunkIndexes = 0x0020;
    private const ushort Chunk48Bit = 0x0040;

    private readonly ErofsVolume _volume;
    private readonly ErofsInode _inode;
    private readonly string _objectId;
    private ErofsCompressionMapper? _compressionMapper;
    private ErofsCompressionExtent _cachedExtent;
    private bool _hasCachedExtent;
    private PooledBufferLease? _decodedExtentLease;
    private ErofsCompressionExtent _decodedExtent;
    private bool _decodedExtentValid;
    private bool _reuseDecodedExtentLease;
    private long _position;
    private bool _disposed;

    internal ErofsFileStream(ErofsVolume volume, ErofsInode inode)
    {
        _volume = volume;
        _inode = inode;
        _objectId = $"nid:{inode.NodeId}";
        if (inode.Size > long.MaxValue)
        {
            throw volume.Corrupt(
                "inode",
                "size",
                inode.DiskOffset + 8,
                Strings.CorruptMetadata,
                $"nid:{inode.NodeId}");
        }

        if (inode.DataLayout == ErofsDataLayout.FlatInline)
            ValidateInlineTail();
        else if (inode.DataLayout == ErofsDataLayout.ChunkBased)
            ValidateChunkFormat();
    }

    public ErofsInode Inode => _inode;
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return (long)_inode.Size;
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
            if (value < 0 || value > Length)
                throw new IOException(Strings.PositionOutsideFile);
            _position = value;
            InvalidateDecodedExtentIfOutsidePosition();
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
        int requested = (int)Math.Min(buffer.Length, Length - _position);
        if (requested == 0) return 0;

        int completed = 0;
        while (completed < requested)
        {
            int read = _inode.DataLayout switch
            {
                ErofsDataLayout.FlatPlain or ErofsDataLayout.FlatInline =>
                    ReadFlat(buffer.Slice(completed, requested - completed)),
                ErofsDataLayout.ChunkBased =>
                    ReadChunk(buffer.Slice(completed, requested - completed)),
                ErofsDataLayout.CompressedFull or ErofsDataLayout.CompressedCompact =>
                    ReadCompressed(buffer.Slice(completed, requested - completed)),
                _ => throw _volume.Unsupported(
                    "inode",
                    "data_layout",
                    _inode.DiskOffset,
                    Strings.UnsupportedFeature,
                    $"nid:{_inode.NodeId}",
                    (ulong)_inode.DataLayout)
            };
            if (read <= 0)
            {
                throw _volume.Corrupt(
                    "file_data",
                    "length",
                    null,
                    Strings.CorruptMetadata,
                    $"nid:{_inode.NodeId}/logical:{_position}");
            }
            completed += read;
            _position += read;
            InvalidateDecodedExtentIfOutsidePosition();
        }

        return completed;
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
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = position;
        return position;
    }

    public override void Flush() => ThrowIfDisposed();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    public override void SetLength(long value) => throw new NotSupportedException(Strings.ReadOnlyStream);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Strings.ReadOnlyStream);
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException(Strings.ReadOnlyStream);

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        _decodedExtentLease?.Dispose();
        _decodedExtentLease = null;
        _decodedExtentValid = false;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private int ReadFlat(Span<byte> destination)
    {
        int blockSize = _volume.Superblock.BlockSize;
        ulong logicalPosition = (ulong)_position;
        ulong mainLength = _inode.DataLayout == ErofsDataLayout.FlatInline
            ? GetInlineMainLength(_inode.Size, blockSize)
            : _inode.Size;

        if (logicalPosition < mainLength)
        {
            int count = checked((int)Math.Min((ulong)destination.Length, mainLength - logicalPosition));
            Span<byte> target = destination[..count];
            if (_inode.DataBlock == ulong.MaxValue)
            {
                target.Clear();
                return count;
            }
            if (_inode.DataBlock >= _volume.Superblock.BlockCount)
            {
                throw _volume.Corrupt(
                    "file_data",
                    "block_address",
                    null,
                    Strings.CorruptMetadata,
                    $"nid:{_inode.NodeId}");
            }

            ulong physical = checked(
                checked(_inode.DataBlock * (uint)blockSize) + logicalPosition);
            if (physical > (ulong)_volume.Superblock.DeclaredLength - (uint)count)
            {
                throw _volume.Corrupt(
                    "file_data",
                    "physical_range",
                    null,
                    Strings.CorruptMetadata,
                    $"nid:{_inode.NodeId}/logical:{logicalPosition}");
            }
            _volume.ReadExactlyAt(
                (long)physical,
                target,
                "file_data",
                _objectId);
            return count;
        }

        ulong tailOffset = logicalPosition - mainLength;
        ulong tailLength = _inode.Size - mainLength;
        int tailCount = checked((int)Math.Min((ulong)destination.Length, tailLength - tailOffset));
        long metadataStart = checked(_inode.DiskOffset + _inode.InodeSize + _inode.XattrSize);
        _volume.ReadExactlyAt(
            checked(metadataStart + (long)tailOffset),
            destination[..tailCount],
            "inline_data",
            _objectId);
        return tailCount;
    }

    private int ReadChunk(Span<byte> destination)
    {
        int blockSize = _volume.Superblock.BlockSize;
        int chunkBits = checked(_volume.Superblock.BlockSizeBits + (_inode.ChunkFormat & ChunkBlockBitsMask));
        ulong chunkSize = 1UL << chunkBits;
        ulong logicalPosition = (ulong)_position;
        ulong chunkNumber = logicalPosition >> chunkBits;
        ulong chunkStart = chunkNumber << chunkBits;
        ulong offsetInChunk = logicalPosition - chunkStart;
        int count = checked((int)Math.Min(
            (ulong)destination.Length,
            Math.Min(chunkSize - offsetInChunk, _inode.Size - logicalPosition)));

        bool hasIndexes = (_inode.ChunkFormat & ChunkIndexes) != 0;
        int unit = hasIndexes ? 8 : 4;
        long metadataEnd = checked(_inode.DiskOffset + _inode.InodeSize + _inode.XattrSize);
        long indexBase = AlignUp(metadataEnd, unit);
        long indexOffset = checked(indexBase + checked((long)chunkNumber * unit));
        Span<byte> raw = stackalloc byte[8];
        _volume.ReadExactlyAt(
            indexOffset,
            raw[..unit],
            "chunk_index",
            _objectId);

        ulong blockAddress;
        bool hole;
        if (!hasIndexes)
        {
            blockAddress = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            hole = blockAddress == uint.MaxValue;
        }
        else
        {
            ulong mask = (_inode.ChunkFormat & Chunk48Bit) != 0
                ? (1UL << 48) - 1
                : uint.MaxValue;
            blockAddress = ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(raw) << 32) |
                BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
            blockAddress &= mask;
            hole = blockAddress == mask;
            ushort deviceId = BinaryPrimitives.ReadUInt16LittleEndian(raw[2..]);
            if (!hole && deviceId != 0)
            {
                throw _volume.Unsupported(
                    "chunk_index",
                    "device_id",
                    indexOffset + 2,
                    Strings.UnsupportedFeature,
                    $"nid:{_inode.NodeId}/chunk:{chunkNumber}",
                    deviceId);
            }
        }

        Span<byte> target = destination[..count];
        if (hole)
        {
            target.Clear();
            return count;
        }
        if (blockAddress >= _volume.Superblock.BlockCount)
        {
            throw _volume.Corrupt(
                "chunk_index",
                "block_address",
                indexOffset,
                Strings.CorruptMetadata,
                $"nid:{_inode.NodeId}/chunk:{chunkNumber}");
        }

        ulong physical = checked(checked(blockAddress * (uint)blockSize) + offsetInChunk);
        if (physical > (ulong)_volume.Superblock.DeclaredLength - (uint)count)
        {
            throw _volume.Corrupt(
                "chunk_index",
                "physical_range",
                indexOffset,
                Strings.CorruptMetadata,
                $"nid:{_inode.NodeId}/chunk:{chunkNumber}");
        }
        _volume.ReadExactlyAt(
            (long)physical,
            target,
            "file_data",
            _objectId);
        return count;
    }

    private int ReadCompressed(Span<byte> destination)
    {
        ErofsCompressionExtent extent = GetCompressedExtent((ulong)_position);
        ulong offset = (ulong)_position - extent.LogicalOffset;
        int count = checked((int)Math.Min((ulong)destination.Length, extent.DecodedLength - offset));
        if (_decodedExtentValid && _decodedExtentLease is not null && _decodedExtent == extent)
        {
            GetDecodedSpan(_decodedExtentLease, extent).Slice((int)offset, count).CopyTo(destination);
            return count;
        }

        int requiredLength = checked(
            checked((int)extent.EncodedLength) +
            checked((int)extent.DecodedLength));
        if (_decodedExtentLease is null || _decodedExtentLease.Memory.Length < requiredLength)
        {
            _decodedExtentLease?.Dispose();
            _decodedExtentLease = null;
            _decodedExtentValid = false;
            _reuseDecodedExtentLease = false;
            using ErofsCompressionWorkspace workspace =
                _volume.AcquireCompressionWorkspace(_inode, extent);
            _reuseDecodedExtentLease = workspace.ReusableAcrossExtents;
            _decodedExtentLease = workspace.DetachLease();
        }

        PooledBufferLease lease = _decodedExtentLease;
        try
        {
            DecodeExtent(extent, lease.Memory.Span[..requiredLength]);
        }
        catch
        {
            _decodedExtentValid = false;
            _reuseDecodedExtentLease = false;
            _decodedExtentLease = null;
            lease.Dispose();
            throw;
        }
        _decodedExtent = extent;
        _decodedExtentValid = true;
        GetDecodedSpan(lease, extent).Slice((int)offset, count).CopyTo(destination);
        return count;
    }

    private static Span<byte> GetDecodedSpan(
        PooledBufferLease lease,
        ErofsCompressionExtent extent) =>
        lease.Memory.Span.Slice(
            checked((int)extent.EncodedLength),
            checked((int)extent.DecodedLength));

    private void InvalidateDecodedExtentIfOutsidePosition()
    {
        if (!_decodedExtentValid || _decodedExtentLease is null) return;
        ulong position = (ulong)_position;
        if (position >= _decodedExtent.LogicalOffset &&
            position - _decodedExtent.LogicalOffset < _decodedExtent.DecodedLength)
        {
            return;
        }

        _decodedExtentValid = false;
        if (!_reuseDecodedExtentLease)
        {
            _decodedExtentLease.Dispose();
            _decodedExtentLease = null;
        }
    }

    private ErofsCompressionExtent GetCompressedExtent(ulong logicalOffset)
    {
        if (_hasCachedExtent &&
            logicalOffset >= _cachedExtent.LogicalOffset &&
            logicalOffset - _cachedExtent.LogicalOffset < _cachedExtent.DecodedLength)
        {
            return _cachedExtent;
        }

        _compressionMapper ??= new ErofsCompressionMapper(_volume, _inode);
        ErofsCompressionExtent extent = _compressionMapper.Map(logicalOffset);
        _cachedExtent = extent;
        _hasCachedExtent = true;
        return extent;
    }

    private void DecodeExtent(ErofsCompressionExtent extent, Span<byte> memory)
    {
        Span<byte> input = memory[..checked((int)extent.EncodedLength)];
        Span<byte> output = memory.Slice(
            checked((int)extent.EncodedLength),
            checked((int)extent.DecodedLength));
        _volume.ReadExactlyAt(
            checked((long)extent.PhysicalOffset),
            input,
            "compressed_data",
            _objectId);

        switch (extent.Algorithm)
        {
            case ErofsCompressionAlgorithm.Shifted:
                if (output.Length > input.Length)
                    throw BadCompressedExtent(extent, Strings.CorruptMetadata);
                input[..output.Length].CopyTo(output);
                return;

            case ErofsCompressionAlgorithm.Interlaced:
                DecodeInterlaced(extent, input, output);
                return;

            case ErofsCompressionAlgorithm.Lz4:
                DecodeLz4(extent, input, output);
                return;

            case ErofsCompressionAlgorithm.Lzma:
                DecodeLzma(extent, input, output);
                return;

            case ErofsCompressionAlgorithm.Deflate:
                DecodeDeflate(extent, input, output);
                return;

            case ErofsCompressionAlgorithm.Zstd:
                DecodeZstd(extent, input, output);
                return;

            default:
                throw BadCompressedExtent(extent, Strings.CorruptMetadata);
        }
    }

    private void DecodeInterlaced(
        ErofsCompressionExtent extent,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        int blockSize = _volume.Superblock.BlockSize;
        if (input.Length > blockSize || output.Length > input.Length)
            throw BadCompressedExtent(extent, Strings.CorruptMetadata);
        int skip = (int)(extent.LogicalOffset & (uint)(blockSize - 1));
        int right = Math.Min(blockSize - skip, output.Length);
        input.Slice(skip, right).CopyTo(output);
        input[..(output.Length - right)].CopyTo(output[right..]);
    }

    private void DecodeLz4(
        ErofsCompressionExtent extent,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        bool zeroPadding =
            (_volume.Superblock.IncompatibleFeatures & ErofsIncompatibleFeatures.Lz4ZeroPadding) != 0;
        int margin = zeroPadding ? input.IndexOfAnyExcept((byte)0) : 0;
        if (margin < 0)
            throw BadCompressedExtent(extent, Strings.CorruptMetadata);

        ReadOnlySpan<byte> compressed = input[margin..];
        bool partialDecode = extent.PartialReference || !zeroPadding;
        if (!Lz4Decoder.TryDecode(
                compressed,
                output,
                partialDecode,
                out int consumed))
        {
            throw BadCompressedExtent(extent, Strings.CorruptMetadata);
        }

        if (zeroPadding && !extent.PartialReference && consumed != compressed.Length)
        {
            throw BadCompressedExtent(extent, Strings.CorruptMetadata);
        }
    }

    private void DecodeLzma(
        ErofsCompressionExtent extent,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        ReadOnlySpan<byte> compressed = RemoveCompressionPadding(extent, input);
        if (!MicroLzmaDecoder.TryDecode(
                compressed,
                output,
                extent.PartialReference))
        {
            throw BadCompressedExtent(extent, Strings.CorruptMetadata);
        }
    }

    private void DecodeDeflate(
        ErofsCompressionExtent extent,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        ReadOnlySpan<byte> compressed = RemoveCompressionPadding(extent, input);
        if (!DeflateDecoder.TryDecode(
                compressed,
                output,
                extent.PartialReference))
        {
            throw BadCompressedExtent(extent, Strings.CorruptMetadata);
        }
    }

    private void DecodeZstd(
        ErofsCompressionExtent extent,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        ReadOnlySpan<byte> compressed = RemoveCompressionPadding(extent, input);
        if (!ZstdDecoder.TryDecode(
                compressed,
                output,
                extent.PartialReference))
        {
            throw BadCompressedExtent(extent, Strings.CorruptMetadata);
        }
    }

    private ReadOnlySpan<byte> RemoveCompressionPadding(
        ErofsCompressionExtent extent,
        ReadOnlySpan<byte> input)
    {
        int margin = input.IndexOfAnyExcept((byte)0);
        if (margin < 0)
            throw BadCompressedExtent(extent, Strings.CorruptMetadata);
        return input[margin..];
    }

    private Exception BadCompressedExtent(ErofsCompressionExtent extent, string message) =>
        _volume.Corrupt(
            "compressed_data",
            "payload",
            checked((long)extent.PhysicalOffset),
            message,
            $"nid:{_inode.NodeId}/logical:{extent.LogicalOffset}");

    private void ValidateInlineTail()
    {
        int blockSize = _volume.Superblock.BlockSize;
        ulong mainLength = GetInlineMainLength(_inode.Size, blockSize);
        ulong tailLength = _inode.Size - mainLength;
        long tailOffset = checked(_inode.DiskOffset + _inode.InodeSize + _inode.XattrSize);
        int offsetInBlock = (int)(tailOffset & (blockSize - 1));
        if (tailLength > (ulong)(blockSize - offsetInBlock))
        {
            throw _volume.Corrupt(
                "inline_data",
                "range",
                tailOffset,
                Strings.CorruptMetadata,
                $"nid:{_inode.NodeId}");
        }
    }

    private void ValidateChunkFormat()
    {
        int chunkBits = checked(_volume.Superblock.BlockSizeBits + (_inode.ChunkFormat & ChunkBlockBitsMask));
        if (chunkBits >= 63)
        {
            throw _volume.Unsupported(
                "inode",
                "chunk_format",
                _inode.DiskOffset + 16,
                Strings.UnsupportedFeature,
                $"nid:{_inode.NodeId}",
                _inode.ChunkFormat);
        }
        if ((_inode.ChunkFormat & Chunk48Bit) != 0 &&
            (_volume.Superblock.IncompatibleFeatures & ErofsIncompatibleFeatures.Bit48) == 0)
        {
            throw _volume.Corrupt(
                "inode",
                "chunk_format",
                _inode.DiskOffset + 16,
                Strings.CorruptMetadata,
                $"nid:{_inode.NodeId}");
        }
    }

    private static ulong GetInlineMainLength(ulong size, int blockSize) =>
        size == 0 ? 0 : ((size + (uint)blockSize - 1) / (uint)blockSize - 1) * (uint)blockSize;

    private static long AlignUp(long value, int alignment) =>
        checked((value + alignment - 1) & -alignment);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volume.ThrowIfDisposed();
    }
}
