namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record SaharaTargetInfo(
    uint Version,
    uint MinimumVersionSupported,
    uint MaximumPacketSizeSupported,
    SaharaMode Mode,
    ulong? Serial,
    ulong? SblVersion,
    byte[] CaHash,
    SaharaMsmHwInfo MsmHwInfo);