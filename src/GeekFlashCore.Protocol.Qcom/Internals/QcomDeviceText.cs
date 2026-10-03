using System.Text.RegularExpressions;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal static partial class QcomDeviceText
{
    private const int MaximumDisplayLength = 512;
    private static readonly string[] SensitiveMarkers =
    ["token", "signature", "challenge", "digest", "hash", "authentication", "password", "secret",
     "publickey", "privatekey", "public key", "private key", "authresponse", "auth_response", "payload"];

    internal static string ForDisplay(string message)
    {
        if (message.IndexOfAny(['<', '>']) >= 0 ||
            SensitiveMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
            EncodedMaterial().IsMatch(message))
            return Strings.Qcom_DeviceTextRedacted;

        int length = Math.Min(message.Length, MaximumDisplayLength);
        Span<char> buffer = stackalloc char[length];
        for (int i = 0; i < length; i++)
            buffer[i] = char.IsControl(message[i]) ? ' ' : message[i];
        string safe = new(buffer);
        return message.Length > length ? Strings.FormatQcom_DeviceTextTruncated(safe) : safe;
    }

    [GeneratedRegex("[0-9a-fA-F]{32,}|[A-Za-z0-9+/]{64,}={0,2}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EncodedMaterial();
}
