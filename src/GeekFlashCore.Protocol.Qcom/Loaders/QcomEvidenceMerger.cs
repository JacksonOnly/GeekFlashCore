using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Loaders;

public static class QcomEvidenceMerger
{
    public static QcomVendorKind ResolveVendor(
        QcomVendorKind explicitOverride,
        QcomVendorKind? runtimeEvidence,
        QcomVendorKind programmerHint)
    {
        if (explicitOverride != QcomVendorKind.Auto)
            return explicitOverride;
        if (runtimeEvidence is { } runtime && runtime != QcomVendorKind.Auto)
            return runtime;
        return programmerHint is QcomVendorKind.Auto or QcomVendorKind.Generic
            ? QcomVendorKind.Generic
            : programmerHint;
    }

    public static ulong ResolveMaxPayload(
        ulong requested,
        ulong? runtimeSupported,
        ulong? programmerHint,
        ulong safetyMaximum = int.MaxValue)
    {
        if (requested == 0)
            throw new ArgumentOutOfRangeException(nameof(requested));
        if (safetyMaximum == 0)
            throw new ArgumentOutOfRangeException(nameof(safetyMaximum));
        ulong resolved = Math.Min(requested, safetyMaximum);
        if (runtimeSupported is > 0)
            resolved = Math.Min(resolved, runtimeSupported.Value);
        if (programmerHint is > 0)
            resolved = Math.Min(resolved, programmerHint.Value);
        return resolved;
    }
}
