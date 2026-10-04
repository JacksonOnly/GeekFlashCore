using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Loaders;
using GeekFlashCore.Protocol.Qcom.Vendors.Xiaomi;
using GeekFlashCore.Protocol.Qcom.Vendors.Nothing;
using GeekFlashCore.Protocol.Qcom.Vendors.OnePlus;
using GeekFlashCore.Protocol.Qcom.Vendors.Zte;
using GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

namespace GeekFlashCore.Protocol.Qcom.Vendors;

public static class VendorStrategyResolver
{

    public static IVendorFirehoseStrategy Resolve(
        QcomVendorKind explicitOverride, IEnumerable<string>? runtimeEvidence, QcomVendorKind programmerHint) =>
        Resolve(explicitOverride, runtimeEvidence, programmerHint, QcomVendorKind.Generic);

    /// <summary>Resolves explicit configuration, runtime evidence, programmer and Sahara hints in that order.</summary>
    public static IVendorFirehoseStrategy Resolve(
        QcomVendorKind explicitOverride,
        IEnumerable<string>? runtimeEvidence,
        QcomVendorKind programmerHint,
        QcomVendorKind saharaHint)
    {
        QcomVendorKind? runtime = DetectRuntimeVendor(runtimeEvidence, programmerHint, saharaHint);
        return ForVendor(QcomEvidenceMerger.ResolveVendor(explicitOverride, runtime, programmerHint));
    }

    public static QcomVendorKind? DetectRuntimeVendor(IEnumerable<string>? evidence) =>
        DetectRuntimeVendor(evidence, QcomVendorKind.Generic, QcomVendorKind.Generic);

    /// <summary>Detects a runtime vendor, then uses known programmer and Sahara hints; returns null when unresolved.</summary>
    public static QcomVendorKind? DetectRuntimeVendor(IEnumerable<string>? evidence,
        QcomVendorKind programmerHint, QcomVendorKind saharaHint)
    {
        bool nothing = false;
        bool onePlus = false;
        bool xiaomi = false;
        bool oplus = false;
        bool zte = false;
        foreach (string? item in evidence ?? [])
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;
            ReadOnlySpan<char> value = item.AsSpan();
            nothing |= Contains(value, "ntprojectverify") || Contains(value, "checkntfeature") ||
                       Contains(value, "Nothing");
            onePlus |= Contains(value, "demacia") || Contains(value, "setprocstart") ||
                       Contains(value, "setprojmodel") || Contains(value, "setswprojmodel") ||
                       Contains(value, "OnePlus");
            xiaomi |= Contains(value, "Xiaomi") || Contains(value, "Only nop and sig tag") ||
                      Contains(value, "authentication signature");
            oplus |= Contains(value, "getsigndata") || Contains(value, "sha256init") ||
                     Contains(value, "Oplus") || Contains(value, "Oppo");
            zte |= Contains(value, "Oem=ZTE") || Contains(value, "OEM: ZTE") ||
                   Contains(value, "ZTE");
        }

        if (nothing) return QcomVendorKind.Nothing;
        if (onePlus) return QcomVendorKind.OnePlus;
        if (xiaomi) return QcomVendorKind.Xiaomi;
        if (oplus) return QcomVendorKind.Oplus;
        if (zte) return QcomVendorKind.Zte;
        if (programmerHint is not (QcomVendorKind.Auto or QcomVendorKind.Generic) && Enum.IsDefined(programmerHint))
            return programmerHint;
        if (saharaHint is not (QcomVendorKind.Auto or QcomVendorKind.Generic) && Enum.IsDefined(saharaHint))
            return saharaHint;
        return null;
    }

    /// <summary>Maps the Sahara OEM identifier to the stable vendor model.</summary>
    public static QcomVendorKind DetectSaharaVendor(SaharaTargetInfo? target) =>
        QcomLoaderInspector.MapVendor(QcomImageUtils.Constants.QualcommMapping.GetOemType(target?.MsmHwInfo?.OemId));

    public static IVendorFirehoseStrategy ForVendor(QcomVendorKind vendor) => vendor switch
    {
        QcomVendorKind.Xiaomi => XiaomiFirehoseStrategy.Instance,
        QcomVendorKind.Oplus => OplusFirehoseStrategy.Instance,
        QcomVendorKind.OnePlus => OnePlusFirehoseStrategy.Instance,
        QcomVendorKind.Nothing => NothingFirehoseStrategy.Instance,
        QcomVendorKind.Zte => ZteFirehoseStrategy.Instance,
        QcomVendorKind.Auto or QcomVendorKind.Generic => GenericVendorStrategy.Instance,
        _ => new KnownVendorStrategy(vendor, GenericVendorStrategy.Extend())
    };

    private static bool Contains(ReadOnlySpan<char> value, ReadOnlySpan<char> token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private sealed class KnownVendorStrategy(
        QcomVendorKind vendor,
        IReadOnlySet<string> commands) : IVendorFirehoseStrategy
    {
        public QcomVendorKind Vendor { get; } = vendor;
        public IReadOnlySet<string> AllowedCustomCommands { get; } = commands;

        public ConfigureCommand PrepareConfigure(ConfigureCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            return command;
        }
    }
}
