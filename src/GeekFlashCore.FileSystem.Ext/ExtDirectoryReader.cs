using System.Buffers.Binary;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Abstractions.Interfaces;
using GeekFlashCore.FileSystem.Ext.Models;
using GeekFlashCore.FileSystem.Ext.Types;

namespace GeekFlashCore.FileSystem.Ext;

public sealed class ExtDirectoryReader : IFileSystemDirectoryReader
{
    private readonly ExtVolume _volume;
    private readonly ExtInode _directory;
    private readonly ExtFileStream _stream;
    private FileSystemEntry _current;
    private bool _hasCurrent;
    private bool _disposed;

    internal ExtDirectoryReader(ExtVolume volume, ExtInode directory)
    {
        _volume = volume;
        _directory = directory;
        _stream = new ExtFileStream(volume, directory);
    }

    public FileSystemEntry Current
    {
        get
        {
            ThrowIfDisposed();
            if (!_hasCurrent) throw new InvalidOperationException(Strings.DirectoryCursorNoCurrent);
            return _current;
        }
    }

    public bool MoveNext()
    {
        ThrowIfDisposed();
        _hasCurrent = false;
        Span<byte> header = stackalloc byte[8];
        int blockSize = _volume.Superblock.BlockSize;
        bool hasFileType =
            (_volume.Superblock.IncompatibleFeatures & ExtIncompatibleFeatures.DirectoryFileType) != 0;

        while (_stream.Position < _stream.Length)
        {
            long recordOffset = _stream.Position;
            ReadExactly(_stream, header);
            uint inodeNumber = BinaryPrimitives.ReadUInt32LittleEndian(header);
            ushort rawRecordLength = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            int recordLength = DecodeRecordLength(rawRecordLength, blockSize);
            int nameLength = hasFileType
                ? header[6]
                : BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            int remainingInBlock = blockSize - (int)(recordOffset % blockSize);
            if (recordLength < 8 || (recordLength & 3) != 0 || recordLength > remainingInBlock ||
                recordOffset > _stream.Length - recordLength || nameLength > 255 || nameLength > recordLength - 8)
            {
                throw _volume.Corrupt(
                    "directory_entry",
                    "record_length",
                    recordOffset,
                    Strings.CorruptMetadata,
                    $"inode:{_directory.Number}/offset:{recordOffset}");
            }

            byte[] name = new byte[nameLength];
            ReadExactly(_stream, name);
            _stream.Position = checked(recordOffset + recordLength);
            if (inodeNumber == 0) continue;
            if (inodeNumber > _volume.Superblock.InodeCount)
            {
                throw _volume.Corrupt(
                    "directory_entry",
                    "inode",
                    recordOffset,
                    Strings.CorruptMetadata,
                    $"inode:{_directory.Number}/offset:{recordOffset}");
            }

            ExtInode inode = _volume.ReadInode(inodeNumber);
            bool specialName = name.AsSpan().SequenceEqual("."u8) || name.AsSpan().SequenceEqual(".."u8);
            bool encrypted =
                (_directory.Flags & ExtInodeFlags.Encryption) != 0 && !specialName;
            FileSystemName fileName = _volume.CreateDirectoryName(name, encrypted);
            _current = _volume.CreateEntry(inode, fileName);
            _hasCurrent = true;
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }

    private static int DecodeRecordLength(ushort rawLength, int blockSize)
    {
        if (blockSize == 65536 && (rawLength == 0 || rawLength == ushort.MaxValue))
            return 65536;
        return rawLength;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read == 0) throw new EndOfStreamException(Strings.IoFailure);
            total += read;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volume.ThrowIfDisposed();
    }
}
