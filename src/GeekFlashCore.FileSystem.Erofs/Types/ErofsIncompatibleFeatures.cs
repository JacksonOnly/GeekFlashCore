namespace GeekFlashCore.FileSystem.Erofs.Types;

[Flags]
public enum ErofsIncompatibleFeatures : uint
{
    Lz4ZeroPadding = 0x00000001,
    CompressionConfigurations = 0x00000002,
    ChunkedFiles = 0x00000004,
    DeviceTable = 0x00000008,
    CompressedTailPacking = 0x00000010,
    Fragments = 0x00000020,
    XattrPrefixes = 0x00000040,
    Bit48 = 0x00000080,
    Metabox = 0x00000100
}