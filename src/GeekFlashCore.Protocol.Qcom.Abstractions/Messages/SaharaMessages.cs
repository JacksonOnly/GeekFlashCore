
namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record SaharaImageEntryRequest(SaharaTargetInfo TargetInfo);

public record SaharaImageEntryResponse(IReadOnlyList<SaharaImageEntry> Entries);