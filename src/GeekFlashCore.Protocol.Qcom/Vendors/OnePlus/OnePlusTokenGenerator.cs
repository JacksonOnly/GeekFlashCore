using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GeekFlashCore.Protocol.Qcom.Vendors.OnePlus;

internal readonly record struct OnePlusAuthenticationContext(
    OnePlusDeviceProfile Profile,
    ulong Serial,
    string PublicKey,
    long DeviceTimestamp,
    bool SoftwareGeneration)
{
    public (string PublicKey, string Token) CreateProgramToken() =>
        OnePlusTokenGenerator.CreateProgramToken(Profile, Serial, PublicKey, DeviceTimestamp);
}

internal static class OnePlusTokenGenerator
{
    private const string LegacyPostfix = "0iyFR00pPnoqjVNL";
    private const string LegacyVersion = "guacamoles_21_O.22_191107";
    private const string SoftwarePostfix = "c75oVnz8yUgLZObh";
    private const string SoftwareVersion = "billie8_14_E.01_201028";
    private const string DefaultProductKey = "7016147d58e8c038";
    private const string LegacyProductKey = "b2fad511325185e5";

    public static string CreatePublicKey()
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        Span<char> result = stackalloc char[16];
        for (int i = 0; i < result.Length; i++)
            result[i] = alphabet[random[i] % alphabet.Length];
        return new string(result);
    }

    public static (string PublicKey, string Token) CreateLegacyToken(
        OnePlusDeviceProfile profile, ulong serial, string publicKey, long timestamp, bool demacia = false)
    {
        ValidateInputs(profile, publicKey);
        string project = profile.Version == 1 ? profile.ProjectId : profile.CryptoModel!;
        string productKey = profile.ProjectId is "18801" or "18825" ? LegacyProductKey : DefaultProductKey;
        string serialText = serial.ToString(CultureInfo.InvariantCulture);
        string hashToken = Sha256Upper(productKey + project + LegacyPostfix);
        string secret = Sha256Upper("c4b95538c57df231" + project + "0" + serialText + LegacyVersion +
                                    timestamp.ToString(CultureInfo.InvariantCulture) + hashToken + "5b0217457e49381b");
        string data = demacia
            ? "907heavyworkload" + Sha256Upper("2e7006834dafe8ad" + serialText.PadLeft(10, '0') + "a6674c6b039707ff")
            : string.Join(',', project, LegacyPostfix, hashToken, LegacyVersion, "0", serialText,
                timestamp.ToString(CultureInfo.InvariantCulture), secret);
        string token = Encrypt(data, publicKey, demacia, 256);
        return (publicKey, token);
    }

    public static (string PublicKey, string Token) CreateSoftwareToken(
        OnePlusDeviceProfile profile, ulong serial, string publicKey, long deviceTimestamp, long timestamp)
    {
        ValidateInputs(profile, publicKey);
        if (profile.Version != 3 || string.IsNullOrWhiteSpace(profile.CryptoModel))
            throw new ArgumentException("The selected OnePlus profile is not a software-generation profile.", nameof(profile));
        string project = profile.CryptoModel;
        string productKey = DefaultProductKey;
        string serialText = serial.ToString(CultureInfo.InvariantCulture);
        string hashToken = Sha256Upper(productKey + project + SoftwarePostfix);
        string secret = Sha256Upper(productKey + project + serialText + SoftwareVersion +
                                    timestamp.ToString(CultureInfo.InvariantCulture) + hashToken + "8f7359c8a2951e8c");
        string data = string.Join(',', project, SoftwarePostfix, hashToken, "0", "0", SoftwareVersion,
            serialText, Convert.ToUInt32(project, 16).ToString(CultureInfo.InvariantCulture),
            timestamp.ToString(CultureInfo.InvariantCulture), secret);
        string token = Encrypt(data, publicKey, false, 512, deviceTimestamp);
        return (publicKey, token);
    }

    public static (string PublicKey, string Token) CreateProgramToken(
        OnePlusDeviceProfile profile, ulong serial, string publicKey, long deviceTimestamp)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ValidateInputs(profile, publicKey);
        string project = profile.Version == 1 ? profile.ProjectId : profile.CryptoModel!;
        string productKey = profile.ProjectId is "18801" or "18825" ? LegacyProductKey : DefaultProductKey;
        string serialText = serial.ToString(CultureInfo.InvariantCulture);
        string hashToken = Sha256Upper(productKey + project + (profile.Version == 3 ? SoftwarePostfix : LegacyPostfix));
        string secret = profile.Version == 3
            ? Sha256Upper(productKey + project + serialText + SoftwareVersion +
                timestamp.ToString(CultureInfo.InvariantCulture) + hashToken + "8f7359c8a2951e8c")
            : Sha256Upper("c4b95538c57df231" + project + "0" + serialText + LegacyVersion +
                timestamp.ToString(CultureInfo.InvariantCulture) + hashToken + "5b0217457e49381b");
        string data = timestamp.ToString(CultureInfo.InvariantCulture) + "," + secret;
        string token = Encrypt(data, publicKey, false, profile.Version == 3 ? 512 : 256, deviceTimestamp);
        return (publicKey, token);
    }

    private static string Encrypt(string data, string publicKey, bool demacia, int length, long deviceTimestamp = 0)
    {
        byte[] input = new byte[length];
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        if (dataBytes.Length > input.Length)
            throw new ArgumentException("OnePlus token input is too long.", nameof(data));
        dataBytes.CopyTo(input, 0);
        byte[] key;
        byte[] iv;
        if (length == 512)
        {
            Span<byte> timestamp = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(timestamp, deviceTimestamp);
            key = [.. Convert.FromHexString("46A59730BB0D41E8"), .. Encoding.ASCII.GetBytes(publicKey), .. timestamp];
            iv = Convert.FromHexString("DC910D88E3C6EE65F0C744B40230CE40");
        }
        else if (demacia)
        {
            key = [.. Convert.FromHexString("0163A0D1FDE26711"), .. Encoding.ASCII.GetBytes(publicKey), .. Convert.FromHexString("4827C208FBB0E6F0")];
            iv = Convert.FromHexString("96E0790CAE2BB4AF684C36CB0BEC49CE");
        }
        else
        {
            key = [.. Convert.FromHexString("10456387E37E2371"), .. Encoding.ASCII.GetBytes(publicKey), .. Convert.FromHexString("A2D4A0740FD32896")];
            iv = Convert.FromHexString("9D614A1EAC81C9B2D376D74931036379");
        }
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            return Convert.ToHexString(encryptor.TransformFinalBlock(input, 0, input.Length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string Sha256Upper(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateInputs(OnePlusDeviceProfile profile, string publicKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (publicKey.Length != OnePlusTokenCodec.PublicKeyLength || publicKey.Any(static c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException(Strings.Qcom_OnePlusPublicKeyInvalid, nameof(publicKey));
        if (profile.Version is 2 or 3 && string.IsNullOrWhiteSpace(profile.CryptoModel))
            throw new ArgumentException("The OnePlus profile is missing its crypto model.", nameof(profile));
    }
}
