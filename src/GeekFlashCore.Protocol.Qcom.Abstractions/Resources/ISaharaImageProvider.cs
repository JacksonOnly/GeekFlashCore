namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public interface ISaharaImageProvider
{
    ValueTask<SaharaImageEntryResponse> ResolveAsync(
        SaharaImageEntryRequest request,
        CancellationToken cancellationToken = default);
}
