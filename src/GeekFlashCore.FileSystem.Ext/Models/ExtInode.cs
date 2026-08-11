using GeekFlashCore.FileSystem.Ext.Types;

namespace GeekFlashCore.FileSystem.Ext.Models;

public readonly record struct ExtInode(
    uint Number,
    long DiskOffset,
    ushort Mode,
    uint UserId,
    uint GroupId,
    ulong Size,
    ulong AllocatedSize,
    uint LinkCount,
    ExtInodeFlags Flags,
    uint Generation,
    ulong ExtendedAttributeBlock,
    ushort ExtraInodeSize,
    ExtBlockMap BlockMap,
    uint AccessTime,
    uint ChangeTime,
    uint ModificationTime,
    uint CreationTime)
{
    public ExtInodeType Type => (Mode & 0xF000) switch
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

    public uint GetBlockPointer(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, 15u);
        return BlockMap[index];
    }
}