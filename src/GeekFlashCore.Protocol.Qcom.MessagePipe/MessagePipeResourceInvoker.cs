using GeekFlashCore.Protocol.Qcom.Abstractions;
using MessagePipe;

namespace GeekFlashCore.Protocol.Qcom.MessagePipe;

internal static class MessagePipeResourceInvoker
{
    public static async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        IAsyncRequestHandler<TRequest, TResponse> handler,
        TRequest request,
        string resourceName,
        Func<TResponse, TResponse> validate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            TResponse response = await handler.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            return validate(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (QcomResourceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new QcomResourceException(Strings.FormatResourceResolveFailed(resourceName), exception);
        }
    }
}
