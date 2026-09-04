
namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record SaharaImageEntryRequest(SaharaTargetInfo TargetInfo)
{
    public string? PreferredProgrammerId { get; init; }
    public QcomVendorKind VendorHint { get; init; } = QcomVendorKind.Auto;
    public string? SocHint { get; init; }
}

public sealed record SaharaImageEntryResponse(IReadOnlyList<SaharaImageEntry> Entries);
