using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record OplusDigestResourceRequest(
    QcomTargetInfo TargetInfo,
    OplusDigestMode Mode)
{
    public string? PreferredDigestId { get; init; }
    /// <summary>The provider must supply a Sign rather than relying on a built-in chip signature.</summary>
    public bool RequireSign { get; init; }
    /// <summary>Requests a replacement Sign after one complete, recoverable verification failure.</summary>
    public bool PreviousSignRejected { get; init; }
}

public sealed record OplusDigestResourceResponse(
    IDataSource Digest,
    string? ResourceId = null)
{
    /// <summary>Caller-owned binary Sign, up to 4096 bytes. Core pads it to 4096 bytes on the wire.</summary>
    public IDataSource? Sign { get; init; }
    /// <summary>Pt or Legacy chosen by the host when the request Mode is None; explicit modes cannot be overridden.</summary>
    public OplusDigestMode? SelectedMode { get; init; }
}
