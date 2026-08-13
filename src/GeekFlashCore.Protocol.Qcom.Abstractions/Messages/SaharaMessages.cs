using GeekFlashCore.Protocol.Qcom.Abstractions.Models;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record SaharaImageEntryRequest(SaharaTargetInfo TargetInfo);

public record SaharaImageEntryResponse(IReadOnlyList<SaharaImageEntry> Entries);