namespace GeekFlashCore.Protocol.Qcom.Abstractions;

/// <summary>Provides qdl-compatible signed and chained VIP digest tables.</summary>
public interface IFirehoseVipProvider
{
    ValueTask<FirehoseVipResourceResponse> ResolveAsync(
        FirehoseVipResourceRequest request,
        CancellationToken cancellationToken = default);
}
