using System.Security.Cryptography;
using System.Text;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Xiaomi;

public sealed class XiaomiAuthentication
{
    public const int SignatureLength = 256;

    // Compatibility signatures used by the reference Xiaomi EDL flow. They
    // are tried in order and never written to logs.
    private static readonly string[] BuiltInSignatures =
    [
        "vzXWATo51hZr4Dh+a5sA/Q4JYoP4Ee3oFZSGbPZ2tBsaMupn+6tPbZDkXJRLUzAqHaMtlPMKaOHrEWZysCkgCJqpOPkUZNaSbEKpPQ6uiOVJpJwA/PmxuJ72inzSPevriMAdhQrNUqgyu4ATTEsOKnoUIuJTDBmzCeuh/34SOjTdO4Pc+s3ORfMD0TX+WImeUx4c9xVdSL/xirPl/BouhfuwFd4qPPyO5RqkU/fevEoJWGHaFjfI302c9k7EpfRUhq1z+wNpZblOHujB0/3/7VOkK8KtSvwLkmVF/t9ECiry6G5iVGEOyqMlktNlIAbr2MMYXn6b4Y3GDCkhPJ5LUkQ==",
        "k246jlc8rQfBZ2RLYSF4Ndha1P3bfYQKK3IlQy/NoTp8GSz6l57RZRfmlwsbB99sUW/sgfaWj89//dvDl6Fiwso+XXYSSqF2nxshZLObdpMLTMZ1GffzOYd2d/ToryWChoK8v05ZOlfn4wUyaZJT4LHMXZ0NVUryvUbVbxjW5SkLpKDKwkMfnxnEwaOddmT/q0ip4RpVk4aBmDW4TfVnXnDSX9tRI+ewQP4hEI8K5tfZ0mfyycYa0FTGhJPcTTP3TQzy1Krc1DAVLbZ8IqGBrW13YWN/cMvaiEzcETNyA4N3kOaEXKWodnkwucJv2nEnJWTKNHY9NS9f5Cq3OPs4pQ==",
        "k246jlc8rQfBZ2RLYSF4Ndha1P3bfYQKK3IlQy/NoTp8GSz6l57RZRfmlwsbB99sUW/sgfaWj89//dvDl6Fiwso+XXYSSqF2nxshZLObdpMLTMZ1GffzOYd2d/ToryWChoK8v05ZYAAAEQUyaZJT4LHMXZ0NVUryvUbVbxjW5SkLpKDKwkMfnxnEwaOddmT/q0ip4RpVk4aBmDW4TfVnXnDSX9tRI+ewQP4hEI8K5tfZ0mfyycYa0FTGhJPcTTP3TQzy1Krc1DAVLbZ8IqGBrW13YWN/cMvaiEzcETNyA4N3kOaEXKWodnkwucJv2nEnJWTKNHY9NS9f5Cq3OPs4pQ==",
        "k246jlc8rQfBZ2RLYSF4Ndha1P3bfYQKK3IlQy/NoTp8GSz6l57RZRfmlwsbB99sUW/sgfaWj89//dvDl6Fiwso+XXYSSqF2nxshZLObdpMLTMZ1GffzOYd2d/ToryWChoK8v05ZYAAAAgUyaZJT4LHMXZ0NVUryvUbVbxjW5SkLpKDKwkMfnxnEwaOddmT/q0ip4RpVk4aBmDW4TfVnXnDSX9tRI+ewQP4hEI8K5tfZ0mfyycYa0FTGhJPcTTP3TQzy1Krc1DAVLbZ8IqGBrW13YWN/cMvaiEzcETNyA4N3kOaEXKWodnkwucJv2nEnJWTKNHY9NS9f5Cq3OPs4pQ==",
        "YAAAAQgAk246jlc8rQfBZ2RLYSF4Ndha1P3bfYQKK3IlQy/NoTp8GSz6l57RZRfmlwsbB99sUW/sgfaWj89//dvDl6Fiwso+XXYSSqF2nxshZLObdpMLTMZ1GffzOYd2d/ToryWChoK8v05ZOlfn4wUyaZJT4LHMXZ0NVUryvUbVbxjW5SkLpKDKwkMfnxnEwaOddmT/q0ip4RpVk4aBmDW4TfVnXnDSX9tRI+ewQP4hEI8K5tfZ0mfyycYa0FTGhJPcTTP3TQzy1Krc1DAVLbZ8IqGBrW13YWN/cMvaiEzcETNyA4N3kOaEXKWodnkwucJv2nEnJWTKNHY9NS9f5Cq3OPs4pQ=="
    ];

    private readonly int _transferBufferSize;
    private readonly FirehoseSession _session;

    public XiaomiAuthentication(FirehoseSession session, int transferBufferSize)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (transferBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(transferBufferSize));
        _transferBufferSize = transferBufferSize;
    }

    public SensitiveDataOwner RequestChallenge()
    {
        FirehoseCommandResult result = _session.Execute(new XiaomiSigCommand { TargetName = "req" });
        if (!TryGetChallenge(result, out string challenge))
            throw new InvalidDataException(Strings.Qcom_XiaomiChallengeMissing);
        return SensitiveDataOwner.CopyFrom(Encoding.UTF8.GetBytes(challenge));
    }

    public FirehoseCommandResult Authenticate(ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLength)
            throw new ArgumentException(Strings.FormatQcom_XiaomiSignatureLength(SignatureLength), nameof(signature));
        _session.Execute(new XiaomiSigCommand
        {
            TargetName = "sig",
            SizeInBytes = SignatureLength,
            Verbose = 1
        }, expectedRawMode: true);
        return _session.SendRaw(signature, _transferBufferSize);
    }

    public bool TryAuthenticateBuiltIn(CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < BuiltInSignatures.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] signature = DecodeBuiltInSignature(BuiltInSignatures[index], index);
            try
            {
                FirehoseCommandResult result = Authenticate(signature);
                if (result.IsSuccess && result.Logs.Any(static log =>
                        log.Message.Contains("authenticated", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch (FirehoseNakException exception) when (!exception.Result.RawMode)
            {
                // A rejected XML preamble leaves the session usable for the
                // next compatibility signature.
            }
            catch (FirehoseNakException)
            {
                return false;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }

        return false;
    }

    private static byte[] DecodeBuiltInSignature(string encoded, int index)
    {
        if (index == 0)
            encoded = encoded.Replace("ZblOHujB0/3/7", "ZblOHuj0B3/7", StringComparison.Ordinal);
        else if (index == 4)
            encoded = encoded.Replace("NS9f5Cq3OPs4pQ==", "NS9f5A==", StringComparison.Ordinal);
        return Convert.FromBase64String(encoded);
    }

    private static bool TryGetChallenge(FirehoseCommandResult result, out string challenge)
    {
        if (result.PayloadElements.TryGetValue("sig", out IReadOnlyDictionary<string, string>? attributes) &&
            attributes.TryGetValue("value", out string? payloadValue) && !string.IsNullOrWhiteSpace(payloadValue))
        {
            challenge = payloadValue;
            return true;
        }
        if (result.Attributes.TryGetValue("sig", out string? responseValue) && !string.IsNullOrWhiteSpace(responseValue))
        {
            challenge = responseValue;
            return true;
        }
        challenge = string.Empty;
        return false;
    }
}
