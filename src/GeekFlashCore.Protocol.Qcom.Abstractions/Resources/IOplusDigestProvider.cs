namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public interface IOplusDigestProvider
{
    ValueTask<OplusDigestResourceResponse> ResolveAsync(
        OplusDigestResourceRequest request,
        CancellationToken cancellationToken = default);
}
