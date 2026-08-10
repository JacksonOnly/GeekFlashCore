namespace GeekFlashCore.Android.Sparse.Models;

public class SparseFile
{
    public SparseHeader Header { get; internal set; }
    public IReadOnlyList<SparseChunk> Chunks { get; internal set; } = null!;
}