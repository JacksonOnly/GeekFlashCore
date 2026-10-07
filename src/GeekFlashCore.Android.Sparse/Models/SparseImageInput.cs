namespace GeekFlashCore.Android.Sparse;

/// <summary>A stable, reopenable RAW or Sparse input. Opened streams transfer ownership to the composer.</summary>
/// <param name="OpenStream">Borrowed factory for independent readable, seekable streams.</param>
/// <param name="IsSparse">Whether the source contains a Sparse encoding.</param>
/// <param name="ExpectedLogicalLength">Exact logical length, when supplied; RAW length or expanded Sparse length.</param>
public sealed record SparseImageInput(Func<CancellationToken, Stream> OpenStream, bool IsSparse, long? ExpectedLogicalLength = null);

/// <summary>Places a block-aligned logical window from an input at a position in the output image.</summary>
public readonly record struct SparseImagePlacement(int SourceIndex, long SourceOffset, long OutputOffset, long Length);
