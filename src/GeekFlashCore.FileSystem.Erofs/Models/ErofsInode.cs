using GeekFlashCore.FileSystem.Erofs.Types;

namespace GeekFlashCore.FileSystem.Erofs.Models;

public readonly record struct ErofsInode(
    ulong NodeId,
    long DiskOffset,
    ErofsInodeLayout InodeLayout,
    ErofsDataLayout DataLayout,
    ushort Format,
    ushort XattrIcount,
    int InodeSize,
    int XattrSize,
    ushort Mode,
    uint UserId,
    uint GroupId,
    ulong Size,
    uint LinkCount,
    long ModificationTime,
    uint ModificationTimeNanoseconds,
    ulong DataBlock,
    ulong AllocatedBlocks,
    ushort ChunkFormat,
    bool DotEntryOmitted)
{
    public ErofsInodeType Type => (Mode & 0xF000) switch
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
}