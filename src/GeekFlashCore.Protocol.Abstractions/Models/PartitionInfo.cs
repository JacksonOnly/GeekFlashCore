namespace GeekFlashCore.Protocol.Abstractions;

public class PartitionInfo(string? name,long? offset,long? address,long? length,IReadOnlyDictionary<string,string>? metadata)
{
    public string? Name { get; set; } = name;
    public long? Offset { get; set; } = offset;
    public long? Address { get; set; } = address;
    public long? Length { get; set; } = length;
    public IReadOnlyDictionary<string, string>? Metadata { get; set; } = metadata;
}