using System.Buffers.Binary;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Abstractions.Interfaces;
using GeekFlashCore.FileSystem.Ext.Constants;
using GeekFlashCore.FileSystem.Ext.Internals;
using GeekFlashCore.FileSystem.Ext.Models;
using GeekFlashCore.FileSystem.Ext.Types;

namespace GeekFlashCore.FileSystem.Ext;

public sealed class ExtXattrReader : IFileSystemXattrReader
{
    private readonly ExtVolume _volume;
    private readonly ExtInode _inode;
    private readonly XattrRegion? _inodeRegion;
    private readonly XattrRegion? _blockRegion;
    private XattrRegion _region;
    private int _regionNumber;
    private long _entryOffset;
    private FileSystemXattrEntry _current;
    private ExtXattrDetails _currentDetails;
    private long _currentValueOffset;
    private bool _hasCurrent;
    private bool _disposed;

    internal ExtXattrReader(ExtVolume volume, ExtInode inode)
    {
        _volume = volume;
        _inode = inode;
        _inodeRegion = FindInodeRegion();
        _blockRegion = FindBlockRegion();
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
        _hasCurrent = false;
        Span<byte> entry = stackalloc byte[16];
        Span<byte> marker = entry[..4];

        while (EnsureRegion())
        {
            if (_entryOffset > _region.EndOffset - 4)
            {
                AdvanceRegion();
                continue;
            }

            _volume.ReadExactlyAt(
                _entryOffset,
                marker,
                "xattr_entry",
                $"inode:{_inode.Number}");
            if (BinaryPrimitives.ReadUInt32LittleEndian(marker) == 0)
            {
                AdvanceRegion();
                continue;
            }

            if (_entryOffset > _region.EndOffset - entry.Length)
                throw BadXattr("entry", Strings.CorruptMetadata, _entryOffset);
            _volume.ReadExactlyAt(
                _entryOffset,
                entry,
                "xattr_entry",
                $"inode:{_inode.Number}");

            int nameLength = entry[0];
            byte nameIndex = entry[1];
            ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            uint valueInode = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            uint valueSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            int entryLength = checked((16 + nameLength + 3) & ~3);
            if (_entryOffset > _region.EndOffset - entryLength)
                throw BadXattr("name_length", Strings.CorruptMetadata, _entryOffset);

            byte[] name = new byte[nameLength];
            if (name.Length != 0)
            {
                _volume.ReadExactlyAt(
                    _entryOffset + 16,
                    name,
                    "xattr_entry",
                    $"inode:{_inode.Number}");
            }

            long valueAbsolute = 0;
            if (valueInode == 0)
            {
                valueAbsolute = checked(_region.ValueBaseOffset + valueOffset);
                if (valueAbsolute < _entryOffset + entryLength + 4 ||
                    valueAbsolute > _region.ValueEndOffset ||
                    valueSize > _region.ValueEndOffset - valueAbsolute)
                {
                    throw BadXattr("value_offset", Strings.CorruptMetadata, _entryOffset);
                }
            }
            else
            {
                if ((_volume.Superblock.IncompatibleFeatures & ExtIncompatibleFeatures.ExtendedAttributeInode) == 0 ||
                    valueOffset != 0 || valueInode > _volume.Superblock.InodeCount)
                {
                    throw BadXattr("value_inode", Strings.CorruptMetadata, _entryOffset);
                }
            }

            _entryOffset += entryLength;
            _currentDetails = new ExtXattrDetails(
                nameIndex,
                valueOffset,
                valueInode,
                valueSize,
                hash,
                _entryOffset - entryLength);
            _currentValueOffset = valueAbsolute;
            _current = new FileSystemXattrEntry(
                GetNamespace(nameIndex),
                CreateXattrName(nameIndex, name),
                valueSize,
                _currentDetails);
            _hasCurrent = true;
            return true;
        }

        return false;
    }

    public Stream OpenValue()
    {
        ThrowIfDisposed();
        if (!_hasCurrent) throw new InvalidOperationException(Strings.XattrCursorNoCurrent);
        if (_currentDetails.ValueInode == 0)
        {
            return new ExtRangeStream(
                _volume,
                _currentValueOffset,
                _currentDetails.ValueSize,
                "xattr_value",
                $"inode:{_inode.Number}/xattr:{_currentDetails.EntryOffset}");
        }

        ExtInode valueInode = _volume.ReadInode(_currentDetails.ValueInode);
        if ((valueInode.Flags & ExtInodeFlags.ExtendedAttributeInode) == 0 ||
            valueInode.LinkCount == 0 || valueInode.Size != _currentDetails.ValueSize)
        {
            throw BadXattr(
                "value_inode",
                Strings.CorruptMetadata,
                _currentDetails.EntryOffset);
        }

        return new ExtFileStream(_volume, valueInode);
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private XattrRegion? FindInodeRegion()
    {
        long magicOffset = checked(_inode.DiskOffset + 128 + _inode.ExtraInodeSize);
        long inodeEnd = checked(_inode.DiskOffset + _volume.Superblock.InodeSize);
        if (magicOffset > inodeEnd - 4) return null;
        Span<byte> magic = stackalloc byte[4];
        _volume.ReadExactlyAt(magicOffset, magic, "inode_xattr", $"inode:{_inode.Number}");
        if (BinaryPrimitives.ReadUInt32LittleEndian(magic) != ExtFormat.XattrMagic) return null;
        long entries = magicOffset + 4;
        return new XattrRegion(entries, inodeEnd, entries, inodeEnd);
    }

    private XattrRegion? FindBlockRegion()
    {
        if (_inode.ExtendedAttributeBlock == 0) return null;
        _volume.ValidateBlock(_inode.ExtendedAttributeBlock, "xattr_block", $"inode:{_inode.Number}");
        long blockOffset = checked((long)_inode.ExtendedAttributeBlock * _volume.Superblock.BlockSize);
        Span<byte> header = stackalloc byte[32];
        _volume.ReadExactlyAt(blockOffset, header, "xattr_block", $"inode:{_inode.Number}");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint refCount = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (magic != ExtFormat.XattrMagic || refCount is 0 or > 1024 || blocks != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[28..]) != 0)
        {
            throw BadXattr("header", Strings.CorruptMetadata, blockOffset);
        }

        long end = checked(blockOffset + _volume.Superblock.BlockSize);
        return new XattrRegion(blockOffset + 32, end, blockOffset, end);
    }

    private bool EnsureRegion()
    {
        if (_regionNumber != 0) return _regionNumber <= 2;
        if (_inodeRegion.HasValue)
        {
            _region = _inodeRegion.Value;
            _entryOffset = _region.EntryOffset;
            _regionNumber = 1;
            return true;
        }

        if (_blockRegion.HasValue)
        {
            _region = _blockRegion.Value;
            _entryOffset = _region.EntryOffset;
            _regionNumber = 2;
            return true;
        }

        _regionNumber = 3;
        return false;
    }

    private void AdvanceRegion()
    {
        if (_regionNumber == 1 && _blockRegion.HasValue)
        {
            _region = _blockRegion.Value;
            _entryOffset = _region.EntryOffset;
            _regionNumber = 2;
        }
        else
        {
            _regionNumber = 3;
        }
    }

    private FileSystemName CreateXattrName(byte nameIndex, ReadOnlySpan<byte> name)
    {
        ReadOnlySpan<byte> effectiveName = name;
        if (name.IsEmpty)
        {
            effectiveName = nameIndex switch
            {
                2 => "posix_acl_access"u8,
                3 => "posix_acl_default"u8,
                _ => name
            };
        }

        return _volume.CreateDirectoryName(effectiveName, false);
    }

    private static FileSystemXattrNamespace GetNamespace(byte index) => index switch
    {
        1 => FileSystemXattrNamespace.User,
        2 or 3 or 7 or 8 => FileSystemXattrNamespace.System,
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
            $"inode:{_inode.Number}");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volume.ThrowIfDisposed();
    }

    private readonly record struct XattrRegion(
        long EntryOffset,
        long EndOffset,
        long ValueBaseOffset,
        long ValueEndOffset);
}
