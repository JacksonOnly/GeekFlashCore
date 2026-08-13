namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record SaharaMsmHwInfo(
    uint? AntiRollbackVersion,
    uint? SocHwVersion,
    uint? MsmId,
    uint? OemId,
    uint? ModelId);