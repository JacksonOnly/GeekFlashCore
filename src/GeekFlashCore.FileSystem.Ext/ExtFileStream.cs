using System.Buffers.Binary;
using GeekFlashCore.FileSystem.Ext.Constants;
using GeekFlashCore.FileSystem.Ext.Models;
using GeekFlashCore.FileSystem.Ext.Types;
using GeekFlashCore.IO.BlockDevice;

namespace GeekFlashCore.FileSystem.Ext;

public sealed class ExtFileStream : Stream
{
    private readonly ExtVolume _volume;
    private readonly ExtInode _inode;
    private PooledBufferLease? _workspace;
    private long _position;
    private bool _disposed;
    private ExtBlockMapping _cachedMapping;
    private bool _hasCachedMapping;

    internal ExtFileStream(ExtVolume volume, ExtInode inode)
    {
        _volume = volume;
        _inode = inode;
        if (inode.Size > long.MaxValue)
        {
            throw volume.Corrupt(
                "inode",
                "size",
                inode.DiskOffset + 4,
                Strings.CorruptMetadata,
                $"inode:{inode.Number}");
        }
    }

    public ExtInode Inode => _inode;
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
        int blockSize = _volume.Superblock.BlockSize;
        while (completed < requested)
        {
            ulong logicalBlock = (ulong)(_position / blockSize);
            int offsetInBlock = (int)(_position % blockSize);
            ExtBlockMapping mapping = GetMapping(logicalBlock);
            ulong relativeBlock = logicalBlock - mapping.LogicalBlock;
            ulong available = checked(
                (ulong)mapping.BlockCount * (uint)blockSize -
                relativeBlock * (uint)blockSize -
                (uint)offsetInBlock);
            int count = (int)Math.Min((ulong)(requested - completed), available);
            if (count <= 0)
            {
                throw _volume.Corrupt(
                    "block_map",
                    "length",
                    null,
                    Strings.CorruptMetadata,
                    $"inode:{_inode.Number}/logical:{logicalBlock}");
            }

            Span<byte> destination = buffer.Slice(completed, count);
            if (mapping.IsHole || mapping.Unwritten)
            {
                destination.Clear();
            }
            else
            {
                ulong physicalBlock = checked(mapping.PhysicalBlock + relativeBlock);
                long physicalOffset = checked(
                    checked((long)physicalBlock * blockSize) + offsetInBlock);
                _volume.ReadExactlyAt(
                    physicalOffset,
                    destination,
                    "file_data",
                    $"inode:{_inode.Number}/logical:{logicalBlock}");
            }

            completed += count;
            _position += count;
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
        if (disposing) _workspace?.Dispose();
        _workspace = null;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private ExtBlockMapping GetMapping(ulong logicalBlock)
    {
        if (_hasCachedMapping && _cachedMapping.Contains(logicalBlock))
            return _cachedMapping;

        ExtBlockMapping mapping = (_inode.Flags & ExtInodeFlags.Extents) != 0
            ? MapExtent(logicalBlock)
            : MapLegacy(logicalBlock);
        _cachedMapping = mapping;
        _hasCachedMapping = true;
        return mapping;
    }

    private ExtBlockMapping MapExtent(ulong logicalBlock)
    {
        if (logicalBlock > uint.MaxValue)
            return ExtBlockMapping.Hole(logicalBlock, 1);

        Span<byte> root = stackalloc byte[60];
        for (int index = 0; index < 15; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(root[(index * 4)..], _inode.GetBlockPointer(index));

        ReadOnlySpan<byte> node = root;
        int expectedDepth = -1;
        for (int level = 0; ; level++)
        {
            if (node.Length < 12 || ReadUInt16(node, 0) != ExtFormat.ExtentMagic)
                throw BadExtent("magic", Strings.CorruptMetadata);
            int entries = ReadUInt16(node, 2);
            int maximum = ReadUInt16(node, 4);
            int depth = ReadUInt16(node, 6);
            int capacity = (node.Length - 12) / 12;
            if (entries > maximum || maximum > capacity || depth > _volume.Limits.MaximumMappingDepth)
                throw BadExtent("header", Strings.CorruptMetadata);
            if (expectedDepth >= 0 && depth != expectedDepth)
                throw BadExtent("depth", Strings.CorruptMetadata);

            if (depth == 0)
                return FindLeafMapping(node, entries, (uint)logicalBlock);
            if (entries == 0)
                return ExtBlockMapping.Hole(logicalBlock, 1);

            ulong child = 0;
            uint previousLogical = 0;
            bool selected = false;
            for (int index = 0; index < entries; index++)
            {
                int offset = 12 + index * 12;
                uint entryLogical = ReadUInt32(node, offset);
                if (index != 0 && entryLogical <= previousLogical)
                    throw BadExtent("index", Strings.CorruptMetadata);
                previousLogical = entryLogical;
                if (entryLogical > logicalBlock) break;
                child = ReadUInt32(node, offset + 4) | (ulong)ReadUInt16(node, offset + 8) << 32;
                selected = true;
            }

            if (!selected)
                return ExtBlockMapping.Hole(logicalBlock, 1);
            _volume.ValidateBlock(child, "extent_index", $"inode:{_inode.Number}/level:{level}");
            Span<byte> workspace = GetWorkspace();
            _volume.ReadBlock(child, workspace, "extent_node", $"inode:{_inode.Number}/level:{level}");
            node = workspace[.._volume.Superblock.BlockSize];
            expectedDepth = depth - 1;
        }
    }

    private ExtBlockMapping FindLeafMapping(ReadOnlySpan<byte> node, int entries, uint logicalBlock)
    {
        ulong previousEnd = 0;
        bool havePrevious = false;
        for (int index = 0; index < entries; index++)
        {
            int offset = 12 + index * 12;
            uint extentLogical = ReadUInt32(node, offset);
            ushort rawLength = ReadUInt16(node, offset + 4);
            if (rawLength == 0)
                throw BadExtent("length", Strings.CorruptMetadata);
            bool unwritten = rawLength > 0x8000;
            uint blockCount = unwritten ? (uint)(rawLength - 0x8000) : rawLength;
            if (blockCount == 0)
                throw BadExtent("length", Strings.CorruptMetadata);
            ulong end = (ulong)extentLogical + blockCount;
            if (end > (ulong)uint.MaxValue + 1 || (havePrevious && extentLogical < previousEnd))
                throw BadExtent("logical_block", Strings.CorruptMetadata);

            ulong physical = ReadUInt32(node, offset + 8) | (ulong)ReadUInt16(node, offset + 6) << 32;
            if (physical < _volume.Superblock.FirstDataBlock ||
                physical >= _volume.Superblock.BlockCount ||
                blockCount > _volume.Superblock.BlockCount - physical)
            {
                throw BadExtent("physical_block", Strings.CorruptMetadata);
            }

            if (logicalBlock < extentLogical)
                return ExtBlockMapping.Hole(logicalBlock, extentLogical - logicalBlock);
            if ((ulong)logicalBlock < end)
                return new ExtBlockMapping(extentLogical, blockCount, physical, false, unwritten);

            previousEnd = end;
            havePrevious = true;
        }

        return ExtBlockMapping.Hole(logicalBlock, 1);
    }

    private ExtBlockMapping MapLegacy(ulong logicalBlock)
    {
        ulong pointersPerBlock = (uint)_volume.Superblock.BlockSize / 4u;
        ulong physical;
        if (logicalBlock < 12)
        {
            physical = _inode.GetBlockPointer((int)logicalBlock);
        }
        else
        {
            ulong index = logicalBlock - 12;
            if (index < pointersPerBlock)
            {
                physical = ReadIndirect(_inode.GetBlockPointer(12), index);
            }
            else
            {
                index -= pointersPerBlock;
                ulong doubleCapacity = checked(pointersPerBlock * pointersPerBlock);
                if (index < doubleCapacity)
                {
                    ulong first = ReadIndirect(_inode.GetBlockPointer(13), index / pointersPerBlock);
                    physical = ReadIndirect(first, index % pointersPerBlock);
                }
                else
                {
                    index -= doubleCapacity;
                    ulong tripleCapacity = checked(doubleCapacity * pointersPerBlock);
                    if (index >= tripleCapacity)
                        throw BadExtent("logical_block", Strings.CorruptMetadata);
                    ulong first = ReadIndirect(_inode.GetBlockPointer(14), index / doubleCapacity);
                    ulong second = ReadIndirect(first, index % doubleCapacity / pointersPerBlock);
                    physical = ReadIndirect(second, index % pointersPerBlock);
                }
            }
        }

        if (physical == 0) return ExtBlockMapping.Hole(logicalBlock, 1);
        _volume.ValidateBlock(physical, "indirect_block", $"inode:{_inode.Number}/logical:{logicalBlock}");
        return new ExtBlockMapping(logicalBlock, 1, physical, false, false);
    }

    private ulong ReadIndirect(ulong block, ulong index)
    {
        if (block == 0) return 0;
        _volume.ValidateBlock(block, "indirect_block", $"inode:{_inode.Number}");
        ulong pointersPerBlock = (uint)_volume.Superblock.BlockSize / 4u;
        if (index >= pointersPerBlock)
            throw BadExtent("pointer_index", Strings.CorruptMetadata);
        Span<byte> pointer = stackalloc byte[4];
        long offset = checked(
            checked((long)block * _volume.Superblock.BlockSize) + checked((long)index * 4));
        _volume.ReadExactlyAt(offset, pointer, "indirect_block", $"inode:{_inode.Number}");
        return BinaryPrimitives.ReadUInt32LittleEndian(pointer);
    }

    private Span<byte> GetWorkspace()
    {
        _workspace ??= _volume.WorkingBuffers.Rent(_volume.Superblock.BlockSize);
        return _workspace.Memory.Span[.._volume.Superblock.BlockSize];
    }

    private ushort ReadUInt16(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);

    private uint ReadUInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);

    private Exception BadExtent(string field, string message) =>
        _volume.Corrupt(
            "extent_tree",
            field,
            null,
            message,
            $"inode:{_inode.Number}");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volume.ThrowIfDisposed();
    }

    private readonly record struct ExtBlockMapping(
        ulong LogicalBlock,
        uint BlockCount,
        ulong PhysicalBlock,
        bool IsHole,
        bool Unwritten)
    {
        public bool Contains(ulong logicalBlock) =>
            logicalBlock >= LogicalBlock && logicalBlock - LogicalBlock < BlockCount;

        public static ExtBlockMapping Hole(ulong logicalBlock, ulong blockCount) =>
            new(logicalBlock, checked((uint)Math.Min(blockCount, uint.MaxValue)), 0, true, false);
    }
}
