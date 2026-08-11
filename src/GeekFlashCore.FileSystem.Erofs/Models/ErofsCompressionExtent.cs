using GeekFlashCore.FileSystem.Erofs.Types;

namespace GeekFlashCore.FileSystem.Erofs.Models;

public readonly record struct ErofsCompressionExtent(
    ulong LogicalOffset,
    ulong DecodedLength,
    ulong PhysicalOffset,
    uint EncodedLength,
    ErofsCompressionAlgorithm Algorithm,
    bool PartialReference);
