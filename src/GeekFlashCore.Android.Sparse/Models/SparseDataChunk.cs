using GeekFlashCore.Android.Sparse.Types;

namespace GeekFlashCore.Android.Sparse.Models;

internal readonly record struct SparseDataChunk(
    SparseDataChunkType Type,
    long SourceOffset,
    long Length,
    uint FillPattern);
