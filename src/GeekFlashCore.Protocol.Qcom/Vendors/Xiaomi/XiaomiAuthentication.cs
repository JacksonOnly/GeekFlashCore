using System.Text;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Xiaomi;

public sealed class XiaomiAuthentication
{
    public const int SignatureLength = 256;

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
            throw new InvalidDataException("Xiaomi Firehose returned no signature challenge.");
        return SensitiveDataOwner.CopyFrom(Encoding.UTF8.GetBytes(challenge));
    }

    public FirehoseCommandResult Authenticate(ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLength)
            throw new ArgumentException($"The Xiaomi signature must be {SignatureLength} bytes.", nameof(signature));
        _session.Execute(new XiaomiSigCommand
        {
            TargetName = "sig",
            SizeInBytes = SignatureLength,
            Verbose = 1
        }, expectedRawMode: true);
        return _session.SendRaw(signature, _transferBufferSize);
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
