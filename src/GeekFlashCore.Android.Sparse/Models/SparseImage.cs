namespace GeekFlashCore.Android.Sparse.Models;

public sealed class SparseImage
{
    internal SparseImage(SparseHeader header, IReadOnlyList<SparseRegion> regions)
    {
        Header = header;
        Regions = regions;
    }

    public SparseHeader Header { get; }
    public long RawLength => Header.RawLength;
    public IReadOnlyList<SparseRegion> Regions { get; }
}