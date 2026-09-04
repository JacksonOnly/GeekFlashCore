namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record SaharaTargetInfo
{
    private ReadOnlyMemory<byte>? _caHash;

    public uint Version { get; set; }
    public uint MinimumVersionSupported { get; set; }
    public uint MaximumPacketSizeSupported { get; set; }
    public SaharaMode Mode { get; set; }
    public ulong? Serial { get; set; }
    public ulong? SblVersion { get; set; }
    public ReadOnlyMemory<byte>? CaHash
    {
        get => _caHash;
        set => _caHash = value?.ToArray();
    }
    public SaharaMsmHwInfo? MsmHwInfo { get; set; }
}
