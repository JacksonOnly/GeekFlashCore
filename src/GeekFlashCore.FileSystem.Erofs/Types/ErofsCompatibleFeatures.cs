namespace GeekFlashCore.FileSystem.Erofs.Types;

[Flags]
public enum ErofsCompatibleFeatures : uint
{
    SuperblockChecksum = 0x00000001,
    ModificationTime = 0x00000002,
    XattrFilter = 0x00000004,
    SharedXattrsInMetabox = 0x00000008,
    PlainXattrPrefix = 0x00000010,
    IshareXattrs = 0x00000020
}
