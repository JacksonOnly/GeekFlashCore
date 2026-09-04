using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record OplusDigestResourceRequest(
    QcomTargetInfo TargetInfo,
    OplusDigestMode Mode)
{
    public string? PreferredDigestId { get; init; }
}

public sealed record OplusDigestResourceResponse(
    IDataSource Digest,
    string? ResourceId = null);
