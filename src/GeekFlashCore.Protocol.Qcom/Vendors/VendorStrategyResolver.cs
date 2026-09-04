using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Loaders;

namespace GeekFlashCore.Protocol.Qcom.Vendors;

public static class VendorStrategyResolver
{
    private static readonly IVendorFirehoseStrategy Xiaomi = new KnownVendorStrategy(
        QcomVendorKind.Xiaomi,
        GenericVendorStrategy.Extend("sig"));
    private static readonly IVendorFirehoseStrategy Oplus = new KnownVendorStrategy(
        QcomVendorKind.Oplus,
        GenericVendorStrategy.Extend("digest", "getsigndata", "sha256init", "verify"));
    private static readonly IVendorFirehoseStrategy OnePlus = new KnownVendorStrategy(
        QcomVendorKind.OnePlus,
        GenericVendorStrategy.Extend("demacia", "setprocstart", "setprocend", "setprojmodel", "setswprojmodel"));
    private static readonly IVendorFirehoseStrategy Nothing = new KnownVendorStrategy(
        QcomVendorKind.Nothing,
        GenericVendorStrategy.Extend("checkntfeature", "ntprojectverify"));
    private static readonly IVendorFirehoseStrategy Zte = new KnownVendorStrategy(
        QcomVendorKind.Zte,
        GenericVendorStrategy.Extend(),
        configureOem: "ZTE");

    public static IVendorFirehoseStrategy Resolve(
        QcomVendorKind explicitOverride,
        IEnumerable<string>? runtimeEvidence,
        QcomVendorKind programmerHint)
    {
        QcomVendorKind? runtime = DetectRuntimeVendor(runtimeEvidence);
        return ForVendor(QcomEvidenceMerger.ResolveVendor(explicitOverride, runtime, programmerHint));
    }

    public static QcomVendorKind? DetectRuntimeVendor(IEnumerable<string>? evidence)
    {
        if (evidence is null)
            return null;

        bool nothing = false;
        bool onePlus = false;
        bool xiaomi = false;
        bool oplus = false;
        bool zte = false;
        foreach (string? item in evidence)
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
        return null;
    }

    public static IVendorFirehoseStrategy ForVendor(QcomVendorKind vendor) => vendor switch
    {
        QcomVendorKind.Xiaomi => Xiaomi,
        QcomVendorKind.Oplus => Oplus,
        QcomVendorKind.OnePlus => OnePlus,
        QcomVendorKind.Nothing => Nothing,
        QcomVendorKind.Zte => Zte,
        QcomVendorKind.Auto or QcomVendorKind.Generic => GenericVendorStrategy.Instance,
        _ => new KnownVendorStrategy(vendor, GenericVendorStrategy.Extend())
    };

    private static bool Contains(ReadOnlySpan<char> value, ReadOnlySpan<char> token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private sealed class KnownVendorStrategy(
        QcomVendorKind vendor,
        IReadOnlySet<string> commands,
        string? configureOem = null) : IVendorFirehoseStrategy
    {
        public QcomVendorKind Vendor { get; } = vendor;
        public IReadOnlySet<string> AllowedCustomCommands { get; } = commands;

        public ConfigureCommand PrepareConfigure(ConfigureCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            return configureOem is null ? command : command with { Oem = configureOem };
        }
    }
}
