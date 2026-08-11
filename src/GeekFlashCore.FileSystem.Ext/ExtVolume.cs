using System.Buffers.Binary;
using System.Text;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Ext.Constants;
using GeekFlashCore.FileSystem.Ext.Models;
using GeekFlashCore.FileSystem.Ext.Types;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;
using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.FileSystem.Ext;

public sealed class ExtVolume : IFileSystemVolume
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IReadableBlockDeviceLease _sourceLease;
    private readonly ByteBudget _workingBudget;
    private readonly BudgetedArrayPool _workingBuffers;
    private bool _disposed;

    private ExtVolume(
        IReadableBlockDeviceLease sourceLease,
        ExtSuperblock superblock,
        FileSystemReadLimits limits)
    {
        _sourceLease = sourceLease;
        Superblock = superblock;
        Limits = limits;
        _workingBudget = new ByteBudget(limits.MaximumWorkingBytes);
        _workingBuffers = new BudgetedArrayPool(_workingBudget);

        ExtInode rootInode = ReadInode(2);
        if (rootInode.Type != ExtInodeType.Directory)
            throw Corrupt("inode", "mode", rootInode.DiskOffset, Strings.CorruptMetadata, "inode:2");

        Root = CreateEntry(rootInode, CreateName("/"u8));
        Info = new FileSystemVolumeInfo(
            ExtFormat.FormatId,
            ExtFormat.ResourceKey,
            superblock.Label,
            superblock.Uuid,
            superblock.DeclaredLength,
            superblock.BlockSize,
            false);
    }

    public ExtSuperblock Superblock { get; }
    public FileSystemReadLimits Limits { get; }
    public FileSystemVolumeInfo Info { get; }
    public FileSystemEntry Root { get; }

    internal IReadableBlockDevice Source
    {
        get
        {
            ThrowIfDisposed();
            return _sourceLease.Device;
        }
    }

    internal BudgetedArrayPool WorkingBuffers => _workingBuffers;

    internal static ExtVolume Create(
        IReadableBlockDeviceLease sourceLease,
        ExtSuperblock superblock,
        FileSystemReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(sourceLease);
        ArgumentNullException.ThrowIfNull(superblock);
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            return new ExtVolume(sourceLease, superblock, limits);
        }
        catch
        {
            sourceLease.Dispose();
            throw;
        }
    }

    public ExtGroupDescriptor ReadGroupDescriptor(uint groupNumber)
    {
        ThrowIfDisposed();
        if (groupNumber >= Superblock.GroupCount)
            throw new ArgumentOutOfRangeException(nameof(groupNumber));

        int size = Superblock.DescriptorSize;
        PooledBufferLease? lease = null;
        Span<byte> descriptor = size <= 256
            ? stackalloc byte[size]
            : (lease = _workingBuffers.Rent(size)).Memory.Span[..size];
        try
        {
            long tableOffset = Superblock.BlockSize == 1024 ? 2048 : Superblock.BlockSize;
            long offset = checked(tableOffset + (long)groupNumber * size);
            ReadExactlyAt(offset, descriptor, "group_descriptor", $"group:{groupNumber}");

            ushort storedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[30..]);
            bool hasChecksum =
                (Superblock.ReadOnlyCompatibleFeatures & ExtReadOnlyCompatibleFeatures.GroupDescriptorChecksum) != 0;
            bool checksumVerified = false;
            if (hasChecksum)
            {
                Span<byte> groupBytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(groupBytes, groupNumber);
                ushort checksum = Crc16Helper.Compute(Superblock.UuidBytes.Span);
                checksum = Crc16Helper.Append(checksum, groupBytes);
                checksum = Crc16Helper.Append(checksum, descriptor[..30]);
                if (descriptor.Length > 32)
                    checksum = Crc16Helper.Append(checksum, descriptor[32..]);
                if (checksum != storedChecksum)
                {
                    throw new ExtFileSystemException(Strings.ChecksumMismatch);
                }

                checksumVerified = true;
            }

            ulong blockBitmap = ReadUInt32(descriptor, 0);
            ulong inodeBitmap = ReadUInt32(descriptor, 4);
            ulong inodeTable = ReadUInt32(descriptor, 8);
            uint freeBlocks = ReadUInt16(descriptor, 12);
            uint freeInodes = ReadUInt16(descriptor, 14);
            uint usedDirectories = ReadUInt16(descriptor, 16);
            var flags = (ExtBlockGroupFlags)ReadUInt16(descriptor, 18);
            uint unusedInodes = ReadUInt16(descriptor, 28);

            if (descriptor.Length >= 64)
            {
                blockBitmap |= (ulong)ReadUInt32(descriptor, 32) << 32;
                inodeBitmap |= (ulong)ReadUInt32(descriptor, 36) << 32;
                inodeTable |= (ulong)ReadUInt32(descriptor, 40) << 32;
                freeBlocks |= (uint)ReadUInt16(descriptor, 44) << 16;
                freeInodes |= (uint)ReadUInt16(descriptor, 46) << 16;
                usedDirectories |= (uint)ReadUInt16(descriptor, 48) << 16;
                unusedInodes |= (uint)ReadUInt16(descriptor, 50) << 16;
            }

            if (((ushort)flags & ~0x0007) != 0 || blockBitmap >= Superblock.BlockCount ||
                inodeBitmap >= Superblock.BlockCount || inodeTable >= Superblock.BlockCount ||
                unusedInodes > Superblock.InodesPerGroup)
            {
                throw Corrupt(
                    "group_descriptor",
                    "geometry",
                    offset,
                    Strings.CorruptMetadata,
                    $"group:{groupNumber}");
            }

            ulong inodeTableBlocks =
                ((ulong)Superblock.InodesPerGroup * Superblock.InodeSize + (uint)Superblock.BlockSize - 1) /
                (uint)Superblock.BlockSize;
            if (inodeTableBlocks > Superblock.BlockCount - inodeTable)
            {
                throw Corrupt(
                    "group_descriptor",
                    "inode_table",
                    offset + 8,
                    Strings.CorruptMetadata,
                    $"group:{groupNumber}");
            }

            return new ExtGroupDescriptor(
                groupNumber,
                blockBitmap,
                inodeBitmap,
                inodeTable,
                freeBlocks,
                freeInodes,
                usedDirectories,
                flags,
                unusedInodes,
                storedChecksum,
                checksumVerified);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public ExtInode ReadInode(uint inodeNumber)
    {
        ThrowIfDisposed();
        if (inodeNumber == 0 || inodeNumber > Superblock.InodeCount)
            throw new ArgumentOutOfRangeException(nameof(inodeNumber));

        uint zeroBased = inodeNumber - 1;
        uint groupNumber = zeroBased / Superblock.InodesPerGroup;
        uint indexInGroup = zeroBased % Superblock.InodesPerGroup;
        ExtGroupDescriptor group = ReadGroupDescriptor(groupNumber);
        uint groupInodeCount = Math.Min(
            Superblock.InodesPerGroup,
            Superblock.InodeCount - groupNumber * Superblock.InodesPerGroup);
        if (indexInGroup >= groupInodeCount ||
            indexInGroup >= groupInodeCount - Math.Min(group.UnusedInodeCount, groupInodeCount))
        {
            throw Corrupt(
                "inode",
                "number",
                null,
                Strings.CorruptMetadata,
                $"inode:{inodeNumber}");
        }

        long inodeOffset = checked(
            checked((long)group.InodeTableBlock * Superblock.BlockSize) +
            checked((long)indexInGroup * Superblock.InodeSize));
        int size = Superblock.InodeSize;
        PooledBufferLease? lease = null;
        Span<byte> raw = size <= 512
            ? stackalloc byte[size]
            : (lease = _workingBuffers.Rent(size)).Memory.Span[..size];
        try
        {
            ReadExactlyAt(inodeOffset, raw, "inode", $"inode:{inodeNumber}");
            ushort mode = ReadUInt16(raw, 0);
            ExtInodeType type = ModeToType(mode);
            ulong logicalSize = ReadUInt32(raw, 4);
            if (type is ExtInodeType.RegularFile or ExtInodeType.Directory or ExtInodeType.SymbolicLink)
                logicalSize |= (ulong)ReadUInt32(raw, 108) << 32;

            var flags = (ExtInodeFlags)ReadUInt32(raw, 32);
            ulong blocks = ReadUInt32(raw, 28);
            uint userId = ReadUInt16(raw, 2);
            uint groupId = ReadUInt16(raw, 24);
            ulong xattrBlock = ReadUInt32(raw, 104);
            if (raw.Length >= 128)
            {
                blocks |= (ulong)ReadUInt16(raw, 116) << 32;
                xattrBlock |= (ulong)ReadUInt16(raw, 118) << 32;
                userId |= (uint)ReadUInt16(raw, 120) << 16;
                groupId |= (uint)ReadUInt16(raw, 122) << 16;
            }

            ulong allocatedSize =
                (flags & ExtInodeFlags.HugeFile) != 0 &&
                (Superblock.ReadOnlyCompatibleFeatures & ExtReadOnlyCompatibleFeatures.HugeFile) != 0
                    ? checked(blocks * (uint)Superblock.BlockSize)
                    : checked(blocks * 512UL);
            ushort extraSize = raw.Length >= 130 ? ReadUInt16(raw, 128) : (ushort)0;
            if (extraSize > raw.Length - 128 || (extraSize & 3) != 0)
            {
                throw Corrupt(
                    "inode",
                    "extra_isize",
                    inodeOffset + 128,
                    Strings.CorruptMetadata,
                    $"inode:{inodeNumber}");
            }

            ExtBlockMap map = default;
            for (int index = 0; index < 15; index++)
                map[index] = ReadUInt32(raw, 40 + index * 4);

            uint creationTime = raw.Length >= 148 && extraSize >= 20 ? ReadUInt32(raw, 144) : 0;
            return new ExtInode(
                inodeNumber,
                inodeOffset,
                mode,
                userId,
                groupId,
                logicalSize,
                allocatedSize,
                ReadUInt16(raw, 26),
                flags,
                ReadUInt32(raw, 100),
                xattrBlock,
                extraSize,
                map,
                ReadUInt32(raw, 8),
                ReadUInt32(raw, 12),
                ReadUInt32(raw, 16),
                creationTime);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public IFileSystemDirectoryReader OpenDirectory(FileSystemNodeId nodeId)
    {
        ExtInode inode = GetInode(nodeId);
        if (inode.Type != ExtInodeType.Directory)
            throw new ArgumentException(Strings.NodeNotDirectory, nameof(nodeId));
        return new ExtDirectoryReader(this, inode);
    }

    public Stream OpenRead(FileSystemNodeId nodeId)
    {
        ExtInode inode = GetInode(nodeId);
        if (inode.Type != ExtInodeType.RegularFile)
            throw new ArgumentException(Strings.NodeNotRegularFile, nameof(nodeId));
        EnsureContentReadable(inode);
        return new ExtFileStream(this, inode);
    }

    public FileSystemName ReadSymbolicLink(FileSystemNodeId nodeId)
    {
        ExtInode inode = GetInode(nodeId);
        if (inode.Type != ExtInodeType.SymbolicLink)
            throw new ArgumentException(Strings.NodeNotSymbolicLink, nameof(nodeId));
        EnsureContentReadable(inode);
        if (inode.Size > (ulong)Limits.MaximumSymlinkBytes)
        {
            throw new ExtFileSystemException(Strings.ResourceLimitExceeded);
        }

        byte[] target = new byte[checked((int)inode.Size)];
        if (target.Length <= 60 && inode.AllocatedSize == 0)
        {
            Span<byte> word = stackalloc byte[4];
            for (int index = 0; index < 15 && index * 4 < target.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, inode.GetBlockPointer(index));
                word[..Math.Min(4, target.Length - index * 4)].CopyTo(target.AsSpan(index * 4));
            }
        }
        else
        {
            using var stream = new ExtFileStream(this, inode);
            ReadExactly(stream, target);
        }

        return CreateName(target);
    }

    public IFileSystemXattrReader OpenExtendedAttributes(FileSystemNodeId nodeId) =>
        new ExtXattrReader(this, GetInode(nodeId));

    public object GetNativeDetails(FileSystemNodeId nodeId) => GetInode(nodeId);

    public void Dispose()
    {
        if (_disposed) return;
        _sourceLease.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    internal ExtInode GetInode(FileSystemNodeId nodeId)
    {
        ThrowIfDisposed();
        if (nodeId.Value is 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(nodeId));
        return ReadInode((uint)nodeId.Value);
    }

    internal FileSystemEntry CreateEntry(ExtInode inode, FileSystemName name)
    {
        FileSystemNodeCapabilities capabilities = FileSystemNodeCapabilities.NativeDetails;
        capabilities |= inode.Type switch
        {
            ExtInodeType.RegularFile => FileSystemNodeCapabilities.Read,
            ExtInodeType.Directory => FileSystemNodeCapabilities.Enumerate,
            ExtInodeType.SymbolicLink => FileSystemNodeCapabilities.ReadSymbolicLink,
            _ => FileSystemNodeCapabilities.None
        };
        if (inode.ExtraInodeSize != 0 || inode.ExtendedAttributeBlock != 0)
            capabilities |= FileSystemNodeCapabilities.ReadExtendedAttributes;

        return new FileSystemEntry(
            new FileSystemNodeId(inode.Number),
            name,
            ToNodeType(inode.Type),
            checked((long)inode.Size),
            checked((long)Math.Min(inode.AllocatedSize, long.MaxValue)),
            capabilities);
    }

    internal FileSystemName CreateDirectoryName(ReadOnlySpan<byte> raw, bool encrypted)
    {
        byte[] bytes = raw.ToArray();
        if (encrypted)
            return new FileSystemName(FileSystemNameState.Encrypted, bytes);
        return CreateName(bytes);
    }

    internal void ReadExactlyAt(long offset, Span<byte> destination, string structure, string objectId)
    {
        ThrowIfDisposed();
        if (offset < 0 || offset > Superblock.DeclaredLength - destination.Length)
            throw Corrupt(structure, "range", offset >= 0 ? offset : null, Strings.CorruptMetadata, objectId);
        try
        {
            BlockDeviceIO.ReadExactlyAt(Source, offset, destination);
        }
        catch (EndOfStreamException exception)
        {
            throw new ExtFileSystemException(Strings.IoFailure, exception);
        }
    }

    internal void ReadBlock(ulong block, Span<byte> destination, string structure, string objectId)
    {
        if (destination.Length < Superblock.BlockSize)
            throw new ArgumentException(Strings.DestinationTooSmall, nameof(destination));
        ValidateBlock(block, structure, objectId);
        ReadExactlyAt(
            checked((long)block * Superblock.BlockSize),
            destination[..Superblock.BlockSize],
            structure,
            objectId);
    }

    internal void ValidateBlock(ulong block, string structure, string objectId)
    {
        if (block < Superblock.FirstDataBlock || block >= Superblock.BlockCount)
            throw Corrupt(structure, "block", null, Strings.CorruptMetadata, objectId);
    }

    internal void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ExtVolume));
    }

    internal ExtFileSystemException Corrupt(
        string structure,
        string? field,
        long? offset,
        string message,
        string? objectId = null)
    {
        string context = $"structure: {structure}";
        if (field is not null)
            context += $"; field: {field}";
        if (offset is not null)
            context += $"; offset: {offset}";
        if (objectId is not null)
            context += $"; objectId: {objectId}";
        return new ExtFileSystemException($"{message} ({context})");
    }
    private void EnsureContentReadable(ExtInode inode)
    {
        if ((inode.Flags & ExtInodeFlags.Encryption) != 0)
        {
            throw new ExtFileSystemException(Strings.EncryptionKeyRequired);
        }

        if ((inode.Flags & ExtInodeFlags.InlineData) != 0)
        {
            throw new ExtFileSystemException(Strings.UnsupportedFeature);
        }
    }

    private static FileSystemName CreateName(ReadOnlySpan<byte> raw)
    {
        byte[] bytes = raw.ToArray();
        try
        {
            return new FileSystemName(FileSystemNameState.Plain, bytes, StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return new FileSystemName(FileSystemNameState.Undecodable, bytes);
        }
    }

    private static void ReadExactly(Stream source, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = source.Read(destination[total..]);
            if (read == 0) throw new EndOfStreamException(Strings.IoFailure);
            total += read;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);

    private static ExtInodeType ModeToType(ushort mode) => (mode & 0xF000) switch
    {
        0x1000 => ExtInodeType.Fifo,
        0x2000 => ExtInodeType.CharacterDevice,
        0x4000 => ExtInodeType.Directory,
        0x6000 => ExtInodeType.BlockDevice,
        0x8000 => ExtInodeType.RegularFile,
        0xA000 => ExtInodeType.SymbolicLink,
        0xC000 => ExtInodeType.Socket,
        _ => ExtInodeType.Unknown
    };

    private static FileSystemNodeType ToNodeType(ExtInodeType type) => type switch
    {
        ExtInodeType.Fifo => FileSystemNodeType.Fifo,
        ExtInodeType.CharacterDevice => FileSystemNodeType.CharacterDevice,
        ExtInodeType.Directory => FileSystemNodeType.Directory,
        ExtInodeType.BlockDevice => FileSystemNodeType.BlockDevice,
        ExtInodeType.RegularFile => FileSystemNodeType.RegularFile,
        ExtInodeType.SymbolicLink => FileSystemNodeType.SymbolicLink,
        ExtInodeType.Socket => FileSystemNodeType.Socket,
        _ => FileSystemNodeType.Unknown
    };
}
