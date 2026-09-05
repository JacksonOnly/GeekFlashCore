using GeekFlashCore.Protocol.Qcom.Abstractions;
using MessagePipe;

namespace GeekFlashCore.Protocol.Qcom.MessagePipe;

public sealed class MessagePipeOplusDigestProvider : IOplusDigestProvider
{
    private readonly IAsyncRequestHandler<OplusDigestResourceRequest, OplusDigestResourceResponse> _handler;

    public MessagePipeOplusDigestProvider(
        IAsyncRequestHandler<OplusDigestResourceRequest, OplusDigestResourceResponse> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public ValueTask<OplusDigestResourceResponse> ResolveAsync(
        OplusDigestResourceRequest request,
        CancellationToken cancellationToken = default) =>
        MessagePipeResourceInvoker.InvokeAsync(_handler, request, "Oplus digest", Validate, cancellationToken);

    private static OplusDigestResourceResponse Validate(OplusDigestResourceResponse response)
    {
        if (response?.Digest is null || response.Digest.Length <= 0)
            throw new QcomResourceException(Strings.OplusDigestEmpty);
        return response;
    }
}
