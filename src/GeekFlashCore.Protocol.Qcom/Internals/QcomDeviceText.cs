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
        // Exact diagnostic allowlist: retain useful status/code, never an appended blob.
        if (SafeSignatureStatus().IsMatch(message)) return message.Trim();
        if (DataHashMismatchStatus().IsMatch(message)) return Strings.Qcom_DeviceHashMismatch;
        if (message.IndexOfAny(['<', '>']) >= 0 ||
            SensitiveMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
            EncodedMaterial().IsMatch(message) || SpacedHexMaterial().IsMatch(message))
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

    [GeneratedRegex(@"(?:\b[0-9a-fA-F]{2}[\s,:;-]+){7}[0-9a-fA-F]{2}\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SpacedHexMaterial();

    [GeneratedRegex(@"\A\s*(?:ERROR:\s*)?Hash of data doesn't match the expected hash(?: [+-]?[0-9]{1,10})?\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DataHashMismatchStatus();

    [GeneratedRegex(@"\A\s*(?:ERROR:\s*)?(?:Verifying signature failed with [0-9]{1,10}|Authentication of signed hash failed [0-9]{1,10}|verify passed)\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SafeSignatureStatus();
}
