using System.Text;

namespace GeekFlashCore.Protocol.Qcom.Vendors.OnePlus;

public readonly record struct OnePlusToken(string PublicKey, string Value);

public static class OnePlusTokenCodec
{
    public const int PublicKeyLength = 16;
    public const int LegacyTokenLength = 512;
    public const int SoftwareTokenLength = 1024;

    public static OnePlusToken Decode(ReadOnlySpan<byte> token, string publicKey, bool softwareGeneration = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        if (publicKey.Length != PublicKeyLength || !IsAsciiAlphaNumeric(publicKey))
            throw new ArgumentException(Strings.Qcom_OnePlusPublicKeyInvalid, nameof(publicKey));
        int expectedLength = softwareGeneration ? SoftwareTokenLength : LegacyTokenLength;
        if (token.Length != expectedLength || !IsHex(token))
            throw new ArgumentException(Strings.FormatQcom_OnePlusTokenInvalid(expectedLength), nameof(token));
        return new OnePlusToken(publicKey, Encoding.ASCII.GetString(token));
    }

    public static OnePlusToken Validate(string token, string publicKey, bool softwareGeneration = false)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        if (publicKey.Length != PublicKeyLength || !IsAsciiAlphaNumeric(publicKey))
            throw new ArgumentException(Strings.Qcom_OnePlusPublicKeyInvalid, nameof(publicKey));
        int expectedLength = softwareGeneration ? SoftwareTokenLength : LegacyTokenLength;
        if (token.Length != expectedLength || !IsHex(token))
            throw new ArgumentException(Strings.FormatQcom_OnePlusTokenInvalid(expectedLength), nameof(token));
        return new OnePlusToken(publicKey, token);
    }

    private static bool IsHex(ReadOnlySpan<byte> value)
    {
        foreach (byte character in value)
        {
            if (character is not (>= (byte)'0' and <= (byte)'9') and
                not (>= (byte)'a' and <= (byte)'f') and
                not (>= (byte)'A' and <= (byte)'F'))
                return false;
        }
        return true;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
                return false;
        }
        return true;
    }

    private static bool IsAsciiAlphaNumeric(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character))
                return false;
        }
        return true;
    }
}
