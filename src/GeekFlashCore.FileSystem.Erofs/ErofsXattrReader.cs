using System.Buffers.Binary;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Erofs.Models;
using GeekFlashCore.FileSystem.Erofs.Types;

namespace GeekFlashCore.FileSystem.Erofs;

public sealed class ErofsXattrReader : IFileSystemXattrReader
{
    private const byte LongPrefix = 0x80;

    private readonly ErofsVolume _volume;
    private readonly ErofsInode _inode;
    private readonly long _headerOffset;
    private int _sharedCount;
    private int _sharedIndex;
    private long _inlinePosition;
    private int _inlineRemaining;
    private long _currentValueOffset;
    private ulong _currentValueSourceNodeId;
    private FileSystemXattrEntry _current;
    private bool _initialized;
    private bool _hasCurrent;
    private bool _disposed;

    internal ErofsXattrReader(ErofsVolume volume, ErofsInode inode)
    {
        _volume = volume;
        _inode = inode;
        _headerOffset = checked(inode.DiskOffset + inode.InodeSize);
    }

    public FileSystemXattrEntry Current
    {
        get
        {
            ThrowIfDisposed();
            if (!_hasCurrent) throw new InvalidOperationException(Strings.XattrCursorNoCurrent);
            return _current;
        }
    }

    public bool MoveNext()
    {
        ThrowIfDisposed();
        EnsureInitialized();
        _hasCurrent = false;

        while (_inlineRemaining != 0)
        {
            long entryOffset = _inlinePosition;
            int entrySize = ReadEntry(entryOffset, false, 0);
            if (entrySize > _inlineRemaining)
                throw BadXattr("entry_size", Strings.CorruptMetadata, entryOffset);
            _inlinePosition = checked(_inlinePosition + entrySize);
            _inlineRemaining -= entrySize;
            return true;
        }

        if (_sharedIndex >= _sharedCount) return false;
        Span<byte> rawId = stackalloc byte[4];
        long idOffset = checked(_headerOffset + 12 + checked(_sharedIndex * 4L));
        _volume.ReadExactlyAt(idOffset, rawId, "xattr_header", $"nid:{_inode.NodeId}");
        uint sharedId = BinaryPrimitives.ReadUInt32LittleEndian(rawId);
        _sharedIndex++;
        ulong sharedBase = _volume.Superblock.XattrBlock * (uint)_volume.Superblock.BlockSize;
        ulong idByteOffset = (ulong)sharedId * 4;
        if (sharedBase > ulong.MaxValue - idByteOffset)
            throw BadXattr("shared_id", Strings.CorruptMetadata, idOffset);
        ulong sharedOffset = sharedBase + idByteOffset;
        if (sharedOffset > long.MaxValue)
            throw BadXattr("shared_id", Strings.ResourceLimitExceeded, idOffset);
        ulong sourceNodeId = (_volume.Superblock.CompatibleFeatures &
            ErofsCompatibleFeatures.SharedXattrsInMetabox) != 0
            ? _volume.Superblock.MetaboxNodeId
            : 0;
        if (sourceNodeId == 0 && (_volume.Superblock.CompatibleFeatures &
            ErofsCompatibleFeatures.SharedXattrsInMetabox) != 0)
        {
            throw BadXattr("shared_location", Strings.CorruptMetadata, idOffset);
        }
        _ = ReadEntry((long)sharedOffset, true, sourceNodeId);
        return true;
    }

    public Stream OpenValue()
    {
        ThrowIfDisposed();
        if (!_hasCurrent) throw new InvalidOperationException(Strings.XattrCursorNoCurrent);
        return _volume.OpenMetadataRange(
            _currentValueSourceNodeId,
            _currentValueOffset,
            _current.ValueLength,
            "xattr_value",
            $"nid:{_inode.NodeId}/xattr:{((ErofsXattrDetails)_current.NativeDetails!).EntryOffset}");
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        if (_inode.XattrSize == 0) return;
        if (_inode.XattrSize == 12)
        {
            throw _volume.Unsupported(
                "xattr_header",
                "size",
                _headerOffset,
                Strings.CorruptMetadata,
                $"nid:{_inode.NodeId}",
                0x100000002UL);
        }

        Span<byte> header = stackalloc byte[12];
        _volume.ReadExactlyAt(_headerOffset, header, "xattr_header", $"nid:{_inode.NodeId}");
        if (header[5..].IndexOfAnyExcept((byte)0) >= 0)
            throw BadXattr("reserved", Strings.CorruptMetadata, _headerOffset + 5);
        _sharedCount = header[4];
        int headerSize = checked(12 + _sharedCount * 4);
        if (headerSize > _inode.XattrSize)
            throw BadXattr("shared_count", Strings.CorruptMetadata, _headerOffset + 4);
        _inlinePosition = checked(_headerOffset + headerSize);
        _inlineRemaining = _inode.XattrSize - headerSize;
    }

    private int ReadEntry(long entryOffset, bool shared, ulong sourceNodeId)
    {
        using Stream source = _volume.OpenMetadataSource(
            sourceNodeId,
            "xattr_entry",
            $"nid:{_inode.NodeId}");
        if (entryOffset < 0 || entryOffset > source.Length - 4)
            throw BadXattr("entry_offset", Strings.CorruptMetadata, entryOffset);

        Span<byte> header = stackalloc byte[4];
        ReadExactly(source, entryOffset, header);
        int nameLength = header[0];
        byte nameIndex = header[1];
        ushort valueSize = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);

        int unalignedSize = checked(4 + nameLength + valueSize);
        int entrySize = AlignUp(unalignedSize, 4);
        if (entryOffset > source.Length - entrySize)
            throw BadXattr("entry_size", Strings.CorruptMetadata, entryOffset);

        Span<byte> name = stackalloc byte[nameLength];
        if (nameLength != 0)
        {
            ReadExactly(source, entryOffset + 4, name);
            if (name.IndexOf((byte)0) >= 0)
                throw BadXattr("name", Strings.CorruptMetadata, entryOffset + 4);
        }

        byte effectiveIndex = nameIndex;
        FileSystemName effectiveName;
        Span<byte> combinedName = stackalloc byte[byte.MaxValue * 2 + 32];
        if ((nameIndex & LongPrefix) != 0)
        {
            Span<byte> infix = stackalloc byte[byte.MaxValue];
            ResolvePrefix(
                nameIndex & 0x7F,
                infix,
                out effectiveIndex,
                out int infixLength);
            effectiveName = _volume.CreateName(BuildEffectiveName(
                effectiveIndex,
                infix[..infixLength],
                name,
                combinedName));
        }
        else
        {
            effectiveName = _volume.CreateName(BuildEffectiveName(
                nameIndex,
                default,
                name,
                combinedName));
        }

        _currentValueOffset = checked(entryOffset + 4 + nameLength);
        _currentValueSourceNodeId = sourceNodeId;
        var details = new ErofsXattrDetails(nameIndex, valueSize, entryOffset, shared);
        _current = new FileSystemXattrEntry(
            GetNamespace(effectiveIndex),
            effectiveName,
            valueSize,
            details);
        _hasCurrent = true;
        return entrySize;
    }

    private void ResolvePrefix(
        int prefixId,
        Span<byte> infixDestination,
        out byte baseIndex,
        out int infixLength)
    {
        ErofsSuperblock superblock = _volume.Superblock;
        if ((uint)prefixId >= superblock.XattrPrefixCount)
            throw BadXattr("name_index", Strings.CorruptMetadata, prefixId);

        ulong sourceNodeId = 0;
        if ((superblock.CompatibleFeatures & ErofsCompatibleFeatures.PlainXattrPrefix) == 0)
        {
            sourceNodeId = superblock.MetaboxNodeId != 0
                ? superblock.MetaboxNodeId
                : superblock.PackedNodeId;
        }

        using Stream source = _volume.OpenMetadataSource(
            sourceNodeId,
            "xattr_prefix",
            $"prefix:{prefixId}");
        long position = (long)superblock.XattrPrefixStart << 2;
        Span<byte> rawLength = stackalloc byte[2];
        Span<byte> record = stackalloc byte[byte.MaxValue + 1];
        for (int current = 0; current <= prefixId; current++)
        {
            position = AlignUp(position, 4);
            if (position > source.Length - 2)
                throw BadXattr("prefix_offset", Strings.CorruptMetadata, position);
            ReadExactly(source, position, rawLength);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(rawLength);
            position += 2;
            if (length is < 1 or > byte.MaxValue + 1 || position > source.Length - length)
                throw BadXattr("prefix_length", Strings.CorruptMetadata, position - 2);
            if (current != prefixId)
            {
                position += length;
                continue;
            }

            ReadExactly(source, position, record[..length]);
            baseIndex = record[0];
            infixLength = length - 1;
            ReadOnlySpan<byte> infix = record.Slice(1, infixLength);
            if (infix.IndexOf((byte)0) >= 0)
                throw BadXattr("prefix", Strings.CorruptMetadata, position + 1);
            infix.CopyTo(infixDestination);
            return;
        }

        throw BadXattr("name_index", Strings.CorruptMetadata, prefixId);
    }

    private static ReadOnlySpan<byte> BuildEffectiveName(
        byte index,
        ReadOnlySpan<byte> infix,
        ReadOnlySpan<byte> name,
        Span<byte> destination)
    {
        ReadOnlySpan<byte> baseName = index switch
        {
            2 => "posix_acl_access"u8,
            3 => "posix_acl_default"u8,
            _ => default
        };
        int length = checked(baseName.Length + infix.Length + name.Length);
        baseName.CopyTo(destination);
        infix.CopyTo(destination[baseName.Length..]);
        name.CopyTo(destination[(baseName.Length + infix.Length)..]);
        return destination[..length];
    }

    private static FileSystemXattrNamespace GetNamespace(byte index) => index switch
    {
        1 => FileSystemXattrNamespace.User,
        2 or 3 => FileSystemXattrNamespace.System,
        4 => FileSystemXattrNamespace.Trusted,
        6 => FileSystemXattrNamespace.Security,
        _ => FileSystemXattrNamespace.Unknown
    };

    private Exception BadXattr(string field, string message, long offset) =>
        _volume.Corrupt(
            "xattr",
            field,
            offset,
            message,
            $"nid:{_inode.NodeId}");

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) & -alignment);

    private static long AlignUp(long value, int alignment)
    {
        if (value > long.MaxValue - (alignment - 1))
            throw new OverflowException(Strings.MetadataOffsetTooLarge);
        return (value + alignment - 1) & -alignment;
    }

    private void ReadExactly(Stream source, long offset, Span<byte> destination)
    {
        source.Position = offset;
        int completed = 0;
        while (completed < destination.Length)
        {
            int read = source.Read(destination[completed..]);
            if (read == 0)
                throw BadXattr("range", Strings.IoFailure, offset + completed);
            completed += read;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volume.ThrowIfDisposed();
    }
}
