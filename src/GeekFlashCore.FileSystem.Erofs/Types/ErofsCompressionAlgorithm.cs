namespace GeekFlashCore.FileSystem.Erofs.Types;

public enum ErofsCompressionAlgorithm : byte
{
    Lz4 = 0,
    Lzma = 1,
    Deflate = 2,
    Zstd = 3,
    Shifted = 4,
    Interlaced = 5
}
