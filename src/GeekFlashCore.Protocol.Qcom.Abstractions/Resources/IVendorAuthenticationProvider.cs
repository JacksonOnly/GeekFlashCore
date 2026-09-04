namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public interface IVendorAuthenticationProvider
{
    ValueTask<VendorAuthenticationResourceResponse> ResolveAsync(
        VendorAuthenticationResourceRequest request,
        CancellationToken cancellationToken = default);
}
