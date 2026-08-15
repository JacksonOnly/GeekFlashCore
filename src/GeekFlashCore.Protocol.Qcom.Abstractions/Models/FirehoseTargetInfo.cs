namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record FirehoseTargetInfo
{
    public string? TargetName { get; set; }
    public string? UfsName { get; set; }
    public FirehoseBasicDevInfo? BasicDevCharacteristics { get; set; }
    public IReadOnlyList<FirehoseStorageInfo> StorageInfos { get; set; } = [];
}