namespace GeekFlashCore.FileSystem.Ext.Types;

[Flags]
public enum ExtReadOnlyCompatibleFeatures : uint
{
    SparseSuper = 0x0001,
    LargeFile = 0x0002,
    BtreeDirectory = 0x0004,
    HugeFile = 0x0008,
    GroupDescriptorChecksum = 0x0010,
    DirectoryLinkCount = 0x0020,
    ExtraInodeSize = 0x0040,
    Snapshot = 0x0080,
    Quota = 0x0100,
    BigAlloc = 0x0200,
    MetadataChecksum = 0x0400,
    Replica = 0x0800,
    ReadOnly = 0x1000,
    Project = 0x2000,
    SharedBlocks = 0x4000,
    Verity = 0x8000,
    OrphanPresent = 0x10000
}