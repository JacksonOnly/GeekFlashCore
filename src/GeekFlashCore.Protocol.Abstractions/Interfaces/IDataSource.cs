namespace GeekFlashCore.Protocol.Abstractions;

public interface IDataSource
{
    long Length { get; }
    Stream OpenStream();
    ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default);
}

