namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record FirehoseResponseLog(FirehoseLogLevel Level, string Message);

public record FirehoseResponse<T> : FirehoseResponse
{
    public T? Data { get; init; }

    public FirehoseResponse(FirehoseResponse response, T? data)
        : base(
            response.Logs,
            response.Attributes,
            response.Status,
            response.RawMode,
            response.PayloadElements)
    {
        Data = data;
    }
}

public record FirehoseResponse
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyElements =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    public FirehoseResponse(
        IReadOnlyList<FirehoseResponseLog> logs,
        IReadOnlyDictionary<string, string> attributes,
        FirehoseResponseStatus status,
        bool rawMode,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? payloadElements = null)
    {
        Attributes = attributes;
        Logs = logs;
        Status = status;
        RawMode = rawMode;
        PayloadElements = payloadElements ?? EmptyElements;
    }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PayloadElements { get; init; } =
        EmptyElements;

    public IReadOnlyList<FirehoseResponseLog> Logs { get; init; } = [];
    public FirehoseResponseStatus Status { get; init; } = FirehoseResponseStatus.Nak;
    public bool RawMode { get; init; }
}

public record FirehoseBasicDevInfo
{
    public DateTime BuildDate { get; set; }
    public uint SerialNumber { get; set; }
    public uint? ChipId { get; set; }
    public string? ChipName { get; set; }
    public IReadOnlyList<string> SupportedFunctions { get; set; } = [];
}

public record FirehoseStorageInfo
{
    public FirehoseStorage Storage { get; init; }
    public uint PhysicalPartitionNumber { get; init; }
    public uint? BlockSizeInBytes { get; init; }
    public ulong? BlockCount { get; init; }

    public ulong? CapacityInBytes => BlockSizeInBytes is { } blockSize && BlockCount is { } blockCount
        ? checked(blockSize * blockCount)
        : null;

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<string> RawLogs { get; init; } = [];
}

public record FirehoseConfigureResponse
{
    public string MemoryName { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public ulong MinVersionSupported { get; set; }
    public ulong Version { get; set; }
    public ulong MaxPayloadSizeToTargetInBytes { get; set; }
    public ulong MaxPayloadSizeToTargetInBytesSupported { get; set; }
    public ulong MaxPayloadSizeFromTargetInBytes { get; set; }
    public ulong MaxXmlSizeInBytes { get; set; }
    public ulong MaxDigestTableSizeInBytes { get; set; }
    public DateTime DateTime { get; set; }
}