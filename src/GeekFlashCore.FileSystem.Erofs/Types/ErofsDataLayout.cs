namespace GeekFlashCore.FileSystem.Erofs.Types;

public enum ErofsDataLayout : byte
{
    FlatPlain = 0,
    CompressedFull = 1,
    FlatInline = 2,
    CompressedCompact = 3,
    ChunkBased = 4
}