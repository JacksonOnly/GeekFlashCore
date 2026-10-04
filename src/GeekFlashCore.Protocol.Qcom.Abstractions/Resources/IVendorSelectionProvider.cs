namespace GeekFlashCore.Protocol.Qcom.Abstractions;

/// <summary>Lets a host select a vendor when device and programmer evidence is unavailable.</summary>
public interface IVendorSelectionProvider
{
    /// <summary>Returns an explicit vendor (including Generic), honoring cancellation.</summary>
    ValueTask<VendorSelectionResponse> ResolveAsync(
        VendorSelectionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A snapshot of the target for an unresolved vendor selection.</summary>
public sealed record VendorSelectionRequest(QcomTargetInfo TargetInfo);

/// <summary>The host's explicit vendor selection; Auto is not a valid selection.</summary>
public sealed record VendorSelectionResponse(QcomVendorKind Vendor);
