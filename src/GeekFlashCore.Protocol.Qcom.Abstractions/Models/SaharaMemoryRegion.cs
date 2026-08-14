namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record SaharaMemoryRegion(ulong BaseAddress, ulong Length, string FileName, string Description);
