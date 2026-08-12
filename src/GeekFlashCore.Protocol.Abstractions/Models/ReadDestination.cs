namespace GeekFlashCore.Protocol.Abstractions;

public sealed record ReadDestination : IDisposable
{
    public required StorageTarget Target { get; init; }
    public required Stream OutputStream { get; init; }
    public bool OwnsStream { get; init; } = true;

    public void Dispose()
    {
        if (OwnsStream)
            OutputStream?.Dispose();
    }
}