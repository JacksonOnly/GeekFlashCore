namespace GeekFlashCore.Android.Sparse;

/// <summary>Finite budgets for composing several sparse images of the same geometry.</summary>
public sealed record SparseImageCompositionOptions
{
    /// <summary>Maximum number of ordered, independently reopenable sources.</summary>
    public int MaximumSources { get; init; } = 16;
    /// <summary>Maximum input chunk count and maximum output chunk count.</summary>
    public int MaximumChunks { get; init; } = 262144;
    /// <summary>Mapping budget, reserving 512 bytes per input chunk before parsing.</summary>
    public int MaximumMetadataBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Maximum source streams kept open by one output stream.</summary>
    public int MaximumOpenSources { get; init; } = 4;

    internal void Validate()
    {
        if (MaximumSources is < 1 or > 1024 || MaximumChunks is < 1 or > 1000000 ||
            MaximumMetadataBytes is < 512 or > 128 * 1024 * 1024 || MaximumOpenSources is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(SparseImageCompositionOptions), Strings.CompositionInvalidOptions);
    }
}
