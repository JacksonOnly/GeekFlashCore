using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record FirehoseDigestResourceRequest(QcomTargetInfo TargetInfo);

public sealed record FirehoseDigestResourceResponse(IDataSource Digest, string? ResourceId = null);
