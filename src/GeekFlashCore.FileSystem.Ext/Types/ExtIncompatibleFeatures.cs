namespace GeekFlashCore.FileSystem.Ext.Types;

[Flags]
public enum ExtIncompatibleFeatures : uint
{
    Compression = 0x0001,
    DirectoryFileType = 0x0002,
    NeedsRecovery = 0x0004,
    JournalDevice = 0x0008,
    MetaBlockGroups = 0x0010,
    Extents = 0x0040,
    Bit64 = 0x0080,
    MultiMountProtection = 0x0100,
    FlexibleBlockGroups = 0x0200,
    ExtendedAttributeInode = 0x0400,
    DirectoryData = 0x1000,
    ChecksumSeed = 0x2000,
    LargeDirectory = 0x4000,
    InlineData = 0x8000,
    Encryption = 0x10000,
    Casefold = 0x20000
}