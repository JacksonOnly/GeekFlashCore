namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record FirehoseCommandResult
{
    public FirehoseResponseStatus Status { get; init; }
    public bool RawMode { get; init; }
    public long BytesTransferred { get; init; }
    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<FirehoseResponseLog> Logs { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PayloadElements { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();
    public ReadOnlyMemory<byte> Sha256Digest { get; init; }

    public bool IsSuccess => Status == FirehoseResponseStatus.Ack;
}
