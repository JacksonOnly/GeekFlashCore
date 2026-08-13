namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record SaharaMsmHwInfo
{
    public uint? AntiRollbackVersion { get; set; }
    public uint? SocHwVersion { get; set; }
    public uint? MsmId { get; set; }
    public uint? OemId { get; set; }
    public uint? ModelId { get; set; }
}