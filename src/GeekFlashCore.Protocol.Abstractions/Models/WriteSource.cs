namespace GeekFlashCore.Protocol.Abstractions;

public record WriteSource
{
    public required IDataSource Source { get; init; }
    public required StorageTarget Target { get; init; }
}
