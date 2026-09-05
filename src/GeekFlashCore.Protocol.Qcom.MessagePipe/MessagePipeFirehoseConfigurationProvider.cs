using GeekFlashCore.Protocol.Qcom.Abstractions;
using MessagePipe;

namespace GeekFlashCore.Protocol.Qcom.MessagePipe;

public sealed class MessagePipeFirehoseConfigurationProvider : IFirehoseConfigurationProvider
{
    private readonly IAsyncRequestHandler<FirehoseConfigurationRequest, FirehoseConfigurationResponse> _handler;

    public MessagePipeFirehoseConfigurationProvider(
        IAsyncRequestHandler<FirehoseConfigurationRequest, FirehoseConfigurationResponse> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public ValueTask<FirehoseConfigurationResponse> ResolveAsync(
        FirehoseConfigurationRequest request,
        CancellationToken cancellationToken = default) =>
        MessagePipeResourceInvoker.InvokeAsync(
            _handler,
            request,
            "Firehose configuration",
            Validate,
            cancellationToken);

    private static FirehoseConfigurationResponse Validate(FirehoseConfigurationResponse response)
    {
        if (response?.Configuration is null)
            throw new QcomResourceException(Strings.FirehoseConfigurationMissing);
        response.Configuration.Validate();
        return response;
    }
}
