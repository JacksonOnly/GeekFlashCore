using System.Collections.Frozen;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public enum QcomAuthenticationKind
{
    XiaomiSignature,
    OplusSignature,
    OnePlusToken,
    OnePlusProjectVerify,
    NothingProjectVerify
}

public sealed record VendorAuthenticationResourceRequest
{
    public VendorAuthenticationResourceRequest(
        QcomAuthenticationKind kind,
        QcomTargetInfo targetInfo,
        SensitiveDataOwner? challenge = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        Kind = kind;
        TargetInfo = targetInfo ?? throw new ArgumentNullException(nameof(targetInfo));
        Challenge = challenge;
        Properties = properties?.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) ??
                     FrozenDictionary<string, string>.Empty;
    }

    public QcomAuthenticationKind Kind { get; }
    public QcomTargetInfo TargetInfo { get; }
    public SensitiveDataOwner? Challenge { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }
}

public sealed record VendorAuthenticationResourceResponse
{
    public VendorAuthenticationResourceResponse(
        SensitiveDataOwner payload,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        Properties = properties?.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) ??
                     FrozenDictionary<string, string>.Empty;
    }

    public SensitiveDataOwner Payload { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }
}
