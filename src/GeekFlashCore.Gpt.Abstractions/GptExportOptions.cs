namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptExportOptions
{
    public GptImageType? ImageType { get; init; }
    public GptPatchMode PatchMode { get; init; }
    public ulong? LastUsableLba { get; init; }
    public bool PreserveFullDiskImage { get; init; } = true;
}
