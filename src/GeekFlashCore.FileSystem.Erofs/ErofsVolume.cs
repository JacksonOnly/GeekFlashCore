using System.Buffers.Binary;
using System.Text;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Erofs.Constants;
using GeekFlashCore.FileSystem.Erofs.Internals;
using GeekFlashCore.FileSystem.Erofs.Models;
using GeekFlashCore.FileSystem.Erofs.Types;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;

namespace GeekFlashCore.FileSystem.Erofs;

public sealed class ErofsVolume : IFileSystemVolume
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IReadableBlockDeviceLease _sourceLease;
    private readonly ByteBudget _workingBudget;
    private readonly BudgetedArrayPool _workingBuffers;
    private readonly ByteBudget _cacheBudget;
    private readonly BudgetedArrayPool _cacheBuffers;
    private bool _disposed;

    private ErofsVolume(
        IReadableBlockDeviceLease sourceLease,
        ErofsSuperblock superblock,
        FileSystemReadLimits limits)
    {
        _sourceLease = sourceLease;
        Superblock = superblock;
        Limits = limits;
        _workingBudget = new ByteBudget(limits.MaximumWorkingBytes);
        _workingBuffers = new BudgetedArrayPool(_workingBudget);
        _cacheBudget = new ByteBudget(limits.MaximumCacheBytes);
        _cacheBuffers = new BudgetedArrayPool(_cacheBudget);

        ErofsInode root = ReadInode(superblock.RootNodeId);
        if (root.Type != ErofsInodeType.Directory)
            throw Corrupt("inode", "mode", root.DiskOffset, Strings.CorruptMetadata, $"nid:{root.NodeId}");
        Root = CreateEntry(root, CreateName("/"u8));
        Info = new FileSystemVolumeInfo(
            ErofsFormat.FormatId,
            ErofsFormat.ResourceKey,
            superblock.VolumeName,
            superblock.Uuid,
            superblock.DeclaredLength,
            superblock.BlockSize,
            (superblock.CompatibleFeatures & ErofsCompatibleFeatures.SuperblockChecksum) != 0);
    }

    public ErofsSuperblock Superblock { get; }
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

    internal static ErofsVolume Create(
        IReadableBlockDeviceLease sourceLease,
        ErofsSuperblock superblock,
        FileSystemReadLimits limits)
    {
        try
        {
            return new ErofsVolume(sourceLease, superblock, limits);
        }
        catch
        {
            sourceLease.Dispose();
            throw;
        }
    }

    public ErofsInode ReadInode(ulong nodeId)
    {
        ThrowIfDisposed();
        if ((nodeId & ~ErofsFormat.NodeIdMask) != 0)
        {
            if ((Superblock.IncompatibleFeatures & ErofsIncompatibleFeatures.Metabox) == 0)
            {
                throw Corrupt(
                    "inode",
                    "nid",
                    null,
                    Strings.CorruptMetadata,
                    $"nid:{nodeId}");
            }
            throw new ErofsFileSystemException(Strings.UnsupportedFeature);
        }

        long offset = ErofsSuperblockReader.GetInodeOffset(Superblock, nodeId);
        if (offset < 0 || offset > Superblock.DeclaredLength - 32)
            throw Corrupt("inode", "nid", null, Strings.CorruptMetadata, $"nid:{nodeId}");

        Span<byte> prefix = stackalloc byte[2];
        ReadExactlyAt(offset, prefix, "inode", $"nid:{nodeId}");
        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(prefix);
        if ((format & ~0x001F) != 0)
            throw Unsupported("inode", "format", offset, Strings.CorruptMetadata, $"nid:{nodeId}", format);
        var inodeLayout = (format & 1) == 0 ? ErofsInodeLayout.Compact : ErofsInodeLayout.Extended;
        int inodeSize = inodeLayout == ErofsInodeLayout.Compact ? 32 : 64;
        Span<byte> raw = stackalloc byte[64];
        ReadExactlyAt(offset, raw[..inodeSize], "inode", $"nid:{nodeId}");

        int layoutValue = (format >> 1) & 7;
        if (layoutValue >= 5)
            throw Unsupported("inode", "data_layout", offset, Strings.UnsupportedFeature, $"nid:{nodeId}", (ulong)layoutValue);
        var dataLayout = (ErofsDataLayout)layoutValue;
        ushort xattrIcount = ReadUInt16(raw, 2);
        int xattrSize = xattrIcount == 0 ? 0 : checked(12 + (xattrIcount - 1) * 4);
        if (offset > Superblock.DeclaredLength - inodeSize - xattrSize)
            throw Corrupt("inode", "xattr_icount", offset + 2, Strings.CorruptMetadata, $"nid:{nodeId}");

        ushort mode = ReadUInt16(raw, 4);
        ErofsInodeType type = ModeToType(mode);
        if (type == ErofsInodeType.Unknown)
            throw Corrupt("inode", "mode", offset + 4, Strings.CorruptMetadata, $"nid:{nodeId}");

        ushort nb = ReadUInt16(raw, 6);
        ulong size;
        uint uid;
        uint gid;
        uint links;
        long mtime;
        uint mtimeNsec;
        ulong dataUnion;
        bool usesHighBlockAddress;
        if (inodeLayout == ErofsInodeLayout.Compact)
        {
            size = ReadUInt32(raw, 8);
            uid = ReadUInt16(raw, 24);
            gid = ReadUInt16(raw, 26);
            bool implicitLink = type != ErofsInodeType.Directory && (format & 0x10) != 0;
            links = implicitLink ? 1u : nb;
            if (!implicitLink) nb = 0;
            usesHighBlockAddress = implicitLink &&
                (Superblock.IncompatibleFeatures & ErofsIncompatibleFeatures.Bit48) != 0;
            uint mtimeDelta = ReadUInt32(raw, 12);
            if (Superblock.Epoch > long.MaxValue - mtimeDelta)
                throw Corrupt("inode", "mtime", offset + 12, Strings.CorruptMetadata, $"nid:{nodeId}");
            mtime = Superblock.Epoch + mtimeDelta;
            mtimeNsec = Superblock.FixedNanoseconds;
            dataUnion = ReadUInt32(raw, 16);
            if (ReadUInt32(raw, 28) != 0)
                throw Corrupt("inode", "reserved", offset + 28, Strings.CorruptMetadata, $"nid:{nodeId}");
        }
        else
        {
            size = ReadUInt64(raw, 8);
            uid = ReadUInt32(raw, 24);
            gid = ReadUInt32(raw, 28);
            links = ReadUInt32(raw, 44);
            mtime = unchecked((long)ReadUInt64(raw, 32));
            mtimeNsec = ReadUInt32(raw, 40);
            dataUnion = ReadUInt32(raw, 16);
            usesHighBlockAddress =
                (Superblock.IncompatibleFeatures & ErofsIncompatibleFeatures.Bit48) != 0;
            if (!raw.Slice(48, 16).IsEmpty && raw.Slice(48, 16).IndexOfAnyExcept((byte)0) >= 0)
                throw Corrupt("inode", "reserved", offset + 48, Strings.CorruptMetadata, $"nid:{nodeId}");
        }

        if (size > long.MaxValue || mtimeNsec >= 1_000_000_000)
            throw Corrupt("inode", "size_or_time", offset + 8, Strings.CorruptMetadata, $"nid:{nodeId}");
        ulong high = usesHighBlockAddress
            ? nb
            : 0UL;
        ulong dataBlock = dataUnion | high << 32;
        ulong allocatedBlocks = dataLayout is ErofsDataLayout.CompressedFull or ErofsDataLayout.CompressedCompact
            ? dataBlock
            : CalculateFlatBlocks(size, dataLayout, Superblock.BlockSize);
        if (dataLayout == ErofsDataLayout.FlatPlain &&
            dataBlock == (usesHighBlockAddress
                ? (1UL << 48) - 1
                : uint.MaxValue))
        {
            dataBlock = ulong.MaxValue;
        }

        ushort chunkFormat = dataLayout == ErofsDataLayout.ChunkBased
            ? (ushort)dataUnion
            : (ushort)0;
        if (dataLayout == ErofsDataLayout.ChunkBased && (dataUnion >> 16) != 0)
            throw Corrupt("inode", "chunk_reserved", offset + 18, Strings.CorruptMetadata, $"nid:{nodeId}");
        if (dataLayout == ErofsDataLayout.ChunkBased && (chunkFormat & ~0x007F) != 0)
            throw Unsupported("inode", "chunk_format", offset + 16, Strings.CorruptMetadata, $"nid:{nodeId}", chunkFormat);

        return new ErofsInode(
            nodeId,
            offset,
            inodeLayout,
            dataLayout,
            format,
            xattrIcount,
            inodeSize,
            xattrSize,
            mode,
            uid,
            gid,
            size,
            links,
            mtime,
            mtimeNsec,
            dataBlock,
            allocatedBlocks,
            chunkFormat,
            type == ErofsInodeType.Directory && (format & 0x10) != 0);
    }

    public IFileSystemDirectoryReader OpenDirectory(FileSystemNodeId nodeId)
    {
        ErofsInode inode = GetInode(nodeId);
        if (inode.Type != ErofsInodeType.Directory)
            throw new ArgumentException(Strings.NodeNotDirectory, nameof(nodeId));
        return new ErofsDirectoryReader(this, inode);
    }

    public Stream OpenRead(FileSystemNodeId nodeId)
    {
        ErofsInode inode = GetInode(nodeId);
        if (inode.Type != ErofsInodeType.RegularFile)
            throw new ArgumentException(Strings.NodeNotRegularFile, nameof(nodeId));
        return new ErofsFileStream(this, inode);
    }

    public FileSystemName ReadSymbolicLink(FileSystemNodeId nodeId)
    {
        ErofsInode inode = GetInode(nodeId);
        if (inode.Type != ErofsInodeType.SymbolicLink)
            throw new ArgumentException(Strings.NodeNotSymbolicLink, nameof(nodeId));
        if (inode.Size > (ulong)Limits.MaximumSymlinkBytes)
        {
            throw new ErofsFileSystemException(Strings.ResourceLimitExceeded);
        }

        byte[] target = new byte[checked((int)inode.Size)];
        using var stream = new ErofsFileStream(this, inode);
        ReadExactly(stream, target);
        return CreateName(target);
    }

    public IFileSystemXattrReader OpenExtendedAttributes(FileSystemNodeId nodeId) =>
        new ErofsXattrReader(this, GetInode(nodeId));

    public object GetNativeDetails(FileSystemNodeId nodeId) => GetInode(nodeId);

    public void Dispose()
    {
        if (_disposed) return;
        _sourceLease.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    internal ErofsInode GetInode(FileSystemNodeId nodeId)
    {
        ThrowIfDisposed();
        return ReadInode(nodeId.Value);
    }

    internal FileSystemEntry CreateEntry(ErofsInode inode, FileSystemName name)
    {
        FileSystemNodeCapabilities capabilities = FileSystemNodeCapabilities.NativeDetails;
        capabilities |= inode.Type switch
        {
            ErofsInodeType.RegularFile => FileSystemNodeCapabilities.Read,
            ErofsInodeType.Directory => FileSystemNodeCapabilities.Enumerate,
            ErofsInodeType.SymbolicLink => FileSystemNodeCapabilities.ReadSymbolicLink,
            _ => FileSystemNodeCapabilities.None
        };
        if (inode.XattrIcount != 0)
            capabilities |= FileSystemNodeCapabilities.ReadExtendedAttributes;
        return new FileSystemEntry(
            new FileSystemNodeId(inode.NodeId),
            name,
            ToNodeType(inode.Type),
            checked((long)inode.Size),
            GetAllocatedSize(inode.AllocatedBlocks, Superblock.BlockSize),
            capabilities);
    }

    internal FileSystemName CreateName(ReadOnlySpan<byte> raw)
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

    internal ErofsCompressionWorkspace AcquireCompressionWorkspace(
        ErofsInode inode,
        ErofsCompressionExtent extent)
    {
        int encodedLength = checked((int)extent.EncodedLength);
        int decodedLength = checked((int)extent.DecodedLength);
        int requiredLength = checked(encodedLength + decodedLength);

        PooledBufferLease? lease = null;
        if (requiredLength <= _cacheBudget.Capacity)
        {
            try
            {
                _ = _cacheBuffers.TryRent(requiredLength, out lease);
            }
            catch (ArgumentOutOfRangeException)
            {
                // An ArrayPool bucket can be larger than the configured cache budget.
            }
            if (lease is not null)
            {
                return new ErofsCompressionWorkspace(
                    lease,
                    reusableAcrossExtents: true);
            }
        }

        try
        {
            _ = _workingBuffers.TryRent(requiredLength, out lease);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw CompressionWorkspaceUnavailable(inode, extent, exception);
        }
        if (lease is null)
        {
            throw CompressionWorkspaceUnavailable(inode, extent);
        }
        return new ErofsCompressionWorkspace(
            lease,
            reusableAcrossExtents: false);
    }

    internal PooledBufferLease AcquireWorkingBuffer(
        int minimumLength,
        string structure,
        string objectId,
        long? offset = null)
    {
        PooledBufferLease? lease;
        try
        {
            _ = _workingBuffers.TryRent(minimumLength, out lease);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw WorkingBufferUnavailable(structure, objectId, offset, exception);
        }
        return lease ?? throw WorkingBufferUnavailable(structure, objectId, offset);
    }

    private static ErofsFileSystemException CompressionWorkspaceUnavailable(
        ErofsInode inode,
        ErofsCompressionExtent extent,
        Exception? innerException = null) => new(
        $"{Strings.ResourceLimitExceeded} (structure: compressed_data; field: workspace; offset: {checked((long)extent.PhysicalOffset)}; object: nid:{inode.NodeId}/logical:{extent.LogicalOffset})",
        innerException);

    private static ErofsFileSystemException WorkingBufferUnavailable(
        string structure,
        string objectId,
        long? offset,
        Exception? innerException = null) => new(
        $"{Strings.ResourceLimitExceeded} (structure: {structure}; field: workspace; offset: {offset}; object: {objectId})",
        innerException);

    internal Stream OpenMetadataSource(ulong nodeId, string structure, string objectId)
    {
        ThrowIfDisposed();
        if (nodeId == 0)
        {
            return new ErofsRangeStream(
                this,
                0,
                Superblock.DeclaredLength,
                structure,
                objectId);
        }

        ErofsInode inode = ReadInode(nodeId);
        if (inode.Type != ErofsInodeType.RegularFile)
        {
            throw Corrupt(
                structure,
                "metadata_nid",
                inode.DiskOffset,
                Strings.InvalidFormat,
                objectId);
        }
        return new ErofsFileStream(this, inode);
    }

    internal Stream OpenMetadataRange(
        ulong nodeId,
        long offset,
        long length,
        string structure,
        string objectId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (nodeId == 0)
        {
            if (offset > Superblock.DeclaredLength - length)
            {
                throw Corrupt(
                    structure,
                    "range",
                    offset,
                    Strings.CorruptMetadata,
                    objectId);
            }
            return new ErofsRangeStream(this, offset, length, structure, objectId);
        }

        Stream source = OpenMetadataSource(nodeId, structure, objectId);
        try
        {
            if (offset > source.Length - length)
            {
                throw Corrupt(
                    structure,
                    "range",
                    offset,
                    Strings.CorruptMetadata,
                    objectId);
            }
            return new ErofsSubstream(source, offset, length);
        }
        catch
        {
            source.Dispose();
            throw;
        }
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
            throw new ErofsFileSystemException(Strings.IoFailure, exception);
        }
    }

    internal void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ErofsVolume));
    }

    internal ErofsFileSystemException Corrupt(
        string structure,
        string? field,
        long? offset,
        string message,
        string? objectId = null) => new(
        $"{message} (structure: {structure}; field: {field}; offset: {offset}; object: {objectId})");

    internal ErofsFileSystemException Unsupported(
        string structure,
        string? field,
        long? offset,
        string message,
        string? objectId,
        ulong featureId) => new(
        $"{message} (structure: {structure}; field: {field}; offset: {offset}; object: {objectId}; feature: {featureId})");

    private static ulong CalculateFlatBlocks(ulong size, ErofsDataLayout layout, int blockSize)
    {
        if (size == 0) return 0;
        return layout == ErofsDataLayout.FlatInline
            ? (size - 1) / (uint)blockSize
            : checked((size + (uint)blockSize - 1) / (uint)blockSize);
    }

    private static long GetAllocatedSize(ulong blocks, int blockSize)
    {
        if (blocks > (ulong)long.MaxValue / (uint)blockSize) return long.MaxValue;
        return (long)(blocks * (uint)blockSize);
    }

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

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
    private static ulong ReadUInt64(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);

    private static ErofsInodeType ModeToType(ushort mode) => (mode & 0xF000) switch
    {
        0x1000 => ErofsInodeType.Fifo,
        0x2000 => ErofsInodeType.CharacterDevice,
        0x4000 => ErofsInodeType.Directory,
        0x6000 => ErofsInodeType.BlockDevice,
        0x8000 => ErofsInodeType.RegularFile,
        0xA000 => ErofsInodeType.SymbolicLink,
        0xC000 => ErofsInodeType.Socket,
        _ => ErofsInodeType.Unknown
    };

    private static FileSystemNodeType ToNodeType(ErofsInodeType type) => type switch
    {
        ErofsInodeType.Fifo => FileSystemNodeType.Fifo,
        ErofsInodeType.CharacterDevice => FileSystemNodeType.CharacterDevice,
        ErofsInodeType.Directory => FileSystemNodeType.Directory,
        ErofsInodeType.BlockDevice => FileSystemNodeType.BlockDevice,
        ErofsInodeType.RegularFile => FileSystemNodeType.RegularFile,
        ErofsInodeType.SymbolicLink => FileSystemNodeType.SymbolicLink,
        ErofsInodeType.Socket => FileSystemNodeType.Socket,
        _ => FileSystemNodeType.Unknown
    };
}
