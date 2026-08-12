namespace GeekFlashCore.Protocol.Abstractions;

public abstract record DataMedium
{
    public long? Size { get; init; }
}

public record BufferMedium : DataMedium
{
    public byte[] Content { get; init; } = Array.Empty<byte>();
    public int Offset { get; init; }
}
public record MemoryMedium : DataMedium
{
    public required IDataSource Content { get; init; }
    public long Length { get; init; }
}

public record FileMedium : DataMedium
{
    public string FilePath { get; init; } = string.Empty;
    public long FileOffset { get; init; } = 0;
}