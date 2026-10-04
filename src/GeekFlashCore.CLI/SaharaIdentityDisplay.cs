using System.Globalization;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using QcomImageUtils.Constants;
using QcomImageUtils.Types;

namespace GeekFlashCore.CLI;

internal static class SaharaIdentityDisplay
{
    internal static string SecureBoot(SecureBootState state) => state switch
    {
        SecureBootState.Enabled => Strings.Cli_Enabled,
        SecureBootState.Disabled => Strings.Cli_Disabled,
        _ => Strings.Cli_UnknownValue
    };

    internal static string Mode(SaharaMode mode) => mode switch
    {
        SaharaMode.Command => Strings.Cli_SaharaCommandMode,
        SaharaMode.ImageTxPending => Strings.Cli_SaharaImagePending,
        SaharaMode.ImageTxComplete => Strings.Cli_SaharaImageComplete,
        SaharaMode.MemoryDebug => Strings.Cli_SaharaMemoryDebug,
        _ => Hex((ulong)mode)
    };

    internal static string Hex(ulong? value, int minimumDigits = 8) => value is { } number
        ? "0x" + number.ToString($"X{minimumDigits}", CultureInfo.InvariantCulture)
        : Strings.Cli_UnknownValue;

    internal static (string Oem, string Soc) Names(QcomTargetInfo target)
    {
        SaharaMsmHwInfo? hardware = target.Sahara?.MsmHwInfo;
        QualcommOemType oem = QualcommMapping.GetOemType(hardware?.OemId);
        string oemName = oem switch
        {
            QualcommOemType.Unknown => Fallback(target.OemName),
            QualcommOemType.OppoOneplusRealme => "OPPO / OnePlus / realme",
            _ => oem.ToString()
        };
        string socName = QualcommMapping.TryGetSocType(hardware?.SocHwVersion, hardware?.MsmId, out var soc)
            ? soc.ToString().ToUpperInvariant()
            : Fallback(target.SocName ?? target.Firehose?.BasicDevCharacteristics?.ChipName);
        return (oemName, socName);
    }

    private static string Fallback(string? name) => string.IsNullOrWhiteSpace(name) ? Strings.Cli_UnknownValue : name;
}
