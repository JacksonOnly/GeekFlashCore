using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal static class FirmwarePath
{
    internal static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string normalized = name.Replace('\\', '/');
        if (normalized.Length is 0 or > 4096 || normalized.Any(c => char.IsControl(c) || c == ':') ||
            normalized.Split('/').Any(s => s.Length == 0 || s is "." or ".."))
            throw new InvalidDataException(Strings.UnsafePath);
        return normalized;
    }
}
