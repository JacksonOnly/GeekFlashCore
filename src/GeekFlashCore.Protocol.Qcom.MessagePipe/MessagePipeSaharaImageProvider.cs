using GeekFlashCore.Protocol.Qcom.Abstractions;
using MessagePipe;

namespace GeekFlashCore.Protocol.Qcom.MessagePipe;

public sealed class MessagePipeSaharaImageProvider : ISaharaImageProvider
{
    private readonly IAsyncRequestHandler<SaharaImageEntryRequest, SaharaImageEntryResponse> _handler;

    public MessagePipeSaharaImageProvider(
        IAsyncRequestHandler<SaharaImageEntryRequest, SaharaImageEntryResponse> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public ValueTask<SaharaImageEntryResponse> ResolveAsync(
        SaharaImageEntryRequest request,
        CancellationToken cancellationToken = default) =>
        MessagePipeResourceInvoker.InvokeAsync(
            _handler,
            request,
            "Sahara image",
            Validate,
            cancellationToken);

    private static SaharaImageEntryResponse Validate(SaharaImageEntryResponse response)
    {
        if (response?.Entries is not { Count: > 0 } entries)
            throw new QcomResourceException(Strings.SaharaImagesMissing);

        var ids = new HashSet<int>();
        foreach (SaharaImageEntry entry in entries)
        {
            if (entry is null || entry.Id < 0 || entry.Length < 0 || entry.DataSource is null ||
                entry.Length != entry.DataSource.Length || !ids.Add(entry.Id))
            {
                throw new QcomResourceException(Strings.SaharaImageInvalid);
            }
        }

        return response;
    }
}
