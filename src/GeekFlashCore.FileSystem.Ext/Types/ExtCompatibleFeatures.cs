namespace GeekFlashCore.FileSystem.Ext.Types;

[Flags]
public enum ExtCompatibleFeatures : uint
{
    DirectoryPreallocation = 0x0001,
    ImagicInodes = 0x0002,
    Journal = 0x0004,
    ExtendedAttributes = 0x0008,
    ResizeInode = 0x0010,
    DirectoryIndex = 0x0020,
    LazyBlockGroups = 0x0040,
    ExcludeBitmap = 0x0100,
    SparseSuper2 = 0x0200,
    FastCommit = 0x0400,
    StableInodes = 0x0800,
    OrphanFile = 0x1000
}