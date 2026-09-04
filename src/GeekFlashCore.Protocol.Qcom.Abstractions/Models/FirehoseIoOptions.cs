namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record FirehoseIoOptions
{
    public ulong? LastSector { get; init; }
    public bool? SkipBadBlock { get; init; }
    public bool? GetSpare { get; init; }
    public bool? EccDisabled { get; init; }
}
