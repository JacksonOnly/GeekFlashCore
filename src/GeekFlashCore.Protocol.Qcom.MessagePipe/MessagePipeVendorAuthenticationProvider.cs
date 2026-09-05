using GeekFlashCore.Protocol.Qcom.Abstractions;
using MessagePipe;

namespace GeekFlashCore.Protocol.Qcom.MessagePipe;

public sealed class MessagePipeVendorAuthenticationProvider : IVendorAuthenticationProvider
{
    private readonly IAsyncRequestHandler<VendorAuthenticationResourceRequest,
        VendorAuthenticationResourceResponse> _handler;

    public MessagePipeVendorAuthenticationProvider(
        IAsyncRequestHandler<VendorAuthenticationResourceRequest, VendorAuthenticationResourceResponse> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public ValueTask<VendorAuthenticationResourceResponse> ResolveAsync(
        VendorAuthenticationResourceRequest request,
        CancellationToken cancellationToken = default) =>
        MessagePipeResourceInvoker.InvokeAsync(
            _handler,
            request,
            "vendor authentication",
            Validate,
            cancellationToken);

    private static VendorAuthenticationResourceResponse Validate(VendorAuthenticationResourceResponse response)
    {
        if (response?.Payload is null || response.Payload.IsDisposed || response.Payload.Length == 0)
            throw new QcomResourceException(Strings.AuthenticationPayloadEmpty);
        return response;
    }
}
