namespace GeekFlashCore.Protocol.Qcom.Abstractions;

/// <summary>Provides a digest table for non-vendor-specific Firehose devices.</summary>
public interface IFirehoseDigestProvider
{
    ValueTask<FirehoseDigestResourceResponse> ResolveAsync(
        FirehoseDigestResourceRequest request,
        CancellationToken cancellationToken = default);
}
