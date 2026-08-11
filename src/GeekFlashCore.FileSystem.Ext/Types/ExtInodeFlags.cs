namespace GeekFlashCore.FileSystem.Ext.Types;

[Flags]
public enum ExtInodeFlags : uint
{
    SecureDeletion = 0x00000001,
    Undelete = 0x00000002,
    Compression = 0x00000004,
    Synchronous = 0x00000008,
    Immutable = 0x00000010,
    AppendOnly = 0x00000020,
    NoDump = 0x00000040,
    NoAccessTime = 0x00000080,
    Encryption = 0x00000800,
    DirectoryIndex = 0x00001000,
    JournalData = 0x00004000,
    NoTail = 0x00008000,
    DirectorySync = 0x00010000,
    TopDirectory = 0x00020000,
    HugeFile = 0x00040000,
    Extents = 0x00080000,
    Verity = 0x00100000,
    ExtendedAttributeInode = 0x00200000,
    EndOfFileBlocks = 0x00400000,
    InlineData = 0x10000000,
    ProjectHierarchy = 0x20000000,
    Casefold = 0x40000000
}