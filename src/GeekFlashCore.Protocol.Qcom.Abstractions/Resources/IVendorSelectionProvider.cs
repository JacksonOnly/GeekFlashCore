namespace GeekFlashCore.Protocol.Qcom.Abstractions;

/// <summary>Lets a host select an unresolved vendor or an Oplus mode after confirmed signed-table receive.</summary>
public interface IVendorSelectionProvider
{
    /// <summary>Returns the requested vendor and optional Oplus mode, honoring cancellation. Generic is valid for ordinary vendor selection.</summary>
    ValueTask<VendorSelectionResponse> ResolveAsync(
        VendorSelectionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A snapshot of the target for a vendor or signed-table mode selection.</summary>
public sealed record VendorSelectionRequest(QcomTargetInfo TargetInfo)
{
    /// <summary>When false, preserve the target's already identified or explicitly configured vendor.</summary>
    public bool RequiresVendorSelection { get; init; } = true;

    /// <summary>The running target is waiting for a signed table; select Oplus/OnePlus and an explicit Pt or Legacy mode.</summary>
    public bool RequiresOplusModeSelection { get; init; }
}

/// <summary>The host's explicit vendor selection; Auto is not a valid selection.</summary>
public sealed record VendorSelectionResponse(QcomVendorKind Vendor)
{
    /// <summary>An explicit Pt or Legacy selection when requested; absent for an ordinary vendor selection.</summary>
    public OplusDigestMode? OplusMode { get; init; }
}
