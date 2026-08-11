using System.Buffers.Binary;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Erofs.Models;
using GeekFlashCore.FileSystem.Erofs.Types;
using GeekFlashCore.BlockDevice;

namespace GeekFlashCore.FileSystem.Erofs;

public sealed class ErofsDirectoryReader : IFileSystemDirectoryReader
{
    private const int DirectoryEntrySize = 12;

    private readonly ErofsVolume _volume;
    private readonly ErofsInode _directory;
    private readonly ErofsFileStream _stream;
    private readonly PooledBufferLease _buffer;
    private int _blockLength;
    private int _entryCount;
    private int _entryIndex;
    private int _expectedNameOffset;
    private long _blockLogicalOffset;
    private FileSystemEntry _current;
    private bool _hasCurrent;
    private bool _disposed;

    internal ErofsDirectoryReader(ErofsVolume volume, ErofsInode directory)
    {
        _volume = volume;
        _directory = directory;
        _stream = new ErofsFileStream(volume, directory);
        try
        {
            _buffer = volume.AcquireWorkingBuffer(
                volume.Superblock.BlockSize,
                "directory",
                $"nid:{directory.NodeId}",
                directory.DiskOffset);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public FileSystemEntry Current
    {
        get
        {
            ThrowIfDisposed();
            if (!_hasCurrent)
                throw new InvalidOperationException(Strings.DirectoryCursorNoCurrent);
            return _current;
        }
    }

    public bool MoveNext()
    {
        ThrowIfDisposed();
        _hasCurrent = false;

        if (_entryIndex >= _entryCount && !LoadNextBlock()) return false;
        ReadOnlySpan<byte> block = _buffer.Memory.Span[.._blockLength];
        int entryOffset = checked(_entryIndex * DirectoryEntrySize);
        ReadOnlySpan<byte> raw = block.Slice(entryOffset, DirectoryEntrySize);
        ulong nodeId = BinaryPrimitives.ReadUInt64LittleEndian(raw);
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw[8..]);
        byte fileType = raw[10];
        if (raw[11] != 0 || fileType >= 8 || nameOffset != _expectedNameOffset)
            throw BadDirectoryEntry("header", Strings.CorruptMetadata, entryOffset);

        int nameEnd;
        if (_entryIndex + 1 < _entryCount)
        {
            int nextEntryOffset = checked((_entryIndex + 1) * DirectoryEntrySize + 8);
            nameEnd = BinaryPrimitives.ReadUInt16LittleEndian(block[nextEntryOffset..]);
            if (nameEnd <= nameOffset || nameEnd > _blockLength)
                throw BadDirectoryEntry("name_offset", Strings.CorruptMetadata, entryOffset);
        }
        else
        {
            int terminator = block[nameOffset..].IndexOf((byte)0);
            nameEnd = terminator < 0 ? _blockLength : nameOffset + terminator;
        }

        int nameLength = nameEnd - nameOffset;
        if (nameLength is < 1 or > 255)
            throw BadDirectoryEntry("name_length", Strings.CorruptMetadata, entryOffset);
        ReadOnlySpan<byte> name = block.Slice(nameOffset, nameLength);
        if (name.IndexOf((byte)0) >= 0 || name.IndexOf((byte)'/') >= 0)
            throw BadDirectoryEntry("name", Strings.CorruptMetadata, entryOffset);

        ErofsInode inode = _volume.ReadInode(nodeId);
        if (fileType != 0 && fileType != ToDirectoryFileType(inode.Type))
            throw BadDirectoryEntry("file_type", Strings.CorruptMetadata, entryOffset);

        _entryIndex++;
        _expectedNameOffset = nameEnd;
        _current = _volume.CreateEntry(inode, _volume.CreateName(name));
        _hasCurrent = true;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer.Dispose();
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool LoadNextBlock()
    {
        if (_stream.Position >= _stream.Length) return false;
        _blockLogicalOffset = _stream.Position;
        _blockLength = (int)Math.Min(
            _volume.Superblock.BlockSize,
            _stream.Length - _stream.Position);
        Span<byte> block = _buffer.Memory.Span[.._blockLength];
        ReadExactly(_stream, block);
        if (_blockLength < DirectoryEntrySize)
            throw BadDirectoryEntry("block_length", Strings.CorruptMetadata, 0);

        int firstNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(block[8..]);
        if (firstNameOffset < DirectoryEntrySize ||
            firstNameOffset > _blockLength ||
            firstNameOffset >= _volume.Superblock.BlockSize ||
            firstNameOffset % DirectoryEntrySize != 0)
        {
            throw BadDirectoryEntry("name_offset", Strings.CorruptMetadata, 0);
        }

        _entryCount = firstNameOffset / DirectoryEntrySize;
        _entryIndex = 0;
        _expectedNameOffset = firstNameOffset;
        return true;
    }

    private Exception BadDirectoryEntry(string field, string message, int blockOffset) =>
        _volume.Corrupt(
            "directory_entry",
            field,
            null,
            message,
            $"nid:{_directory.NodeId}/logical:{checked(_blockLogicalOffset + blockOffset)}");

    private static byte ToDirectoryFileType(ErofsInodeType type) => type switch
    {
        ErofsInodeType.RegularFile => 1,
        ErofsInodeType.Directory => 2,
        ErofsInodeType.CharacterDevice => 3,
        ErofsInodeType.BlockDevice => 4,
        ErofsInodeType.Fifo => 5,
        ErofsInodeType.Socket => 6,
        ErofsInodeType.SymbolicLink => 7,
        _ => 0
    };

    private static void ReadExactly(Stream source, Span<byte> destination)
    {
        int completed = 0;
        while (completed < destination.Length)
        {
            int read = source.Read(destination[completed..]);
            if (read == 0) throw new EndOfStreamException(Strings.IoFailure);
            completed += read;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volume.ThrowIfDisposed();
    }
}
