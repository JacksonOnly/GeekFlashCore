namespace GeekFlashCore.FileSystem.Erofs.Constants;

internal static class ErofsFormat
{
    public const string FormatId = "erofs";
    public const string ResourceKey = "FileSystem.Erofs";
    public const uint Magic = 0xE0F5E1E2;
    public const int SuperblockOffset = 1024;
    public const int SuperblockStructureSize = 144;
    public const int InodeSlotBits = 5;
    public const ulong NodeIdMask = (1UL << 63) - 1;
    public const uint KnownIncompatibleFeatures = 0x000001FF;
}
