namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptParseOptions
{
    public int? SectorSize { get; init; }
    public bool IncludeUnallocatedRegions { get; init; }
    public bool HeaderOnly { get; init; }
    public bool AllowEmptyPartitionTypeId { get; init; } = true;
    
    public bool AllowUnpatchedPartitionGeometry { get; init; } = true;
    public bool ReplaceInvalidPartitionNameData { get; init; }
    public bool RequireRedundantCopiesConsistent { get; init; }
    public GptCrcPolicy CrcPolicy { get; init; } = GptCrcPolicy.Report;
}
