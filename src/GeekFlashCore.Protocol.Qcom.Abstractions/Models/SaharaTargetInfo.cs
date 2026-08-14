namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record SaharaTargetInfo
{
    public uint Version { get; set; }
    public uint MinimumVersionSupported { get; set; }
    public uint MaximumPacketSizeSupported { get; set; }
    public SaharaMode Mode { get; set; }
    public ulong? Serial { get; set; }
    public ulong? SblVersion { get; set; }
    public ReadOnlyMemory<byte>? CaHash { get; set; }
    public SaharaMsmHwInfo? MsmHwInfo { get; set; }
}