using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Configuration;

internal sealed record ConfigureState
{
    public required FirehoseStorage Storage { get; init; }
    public required uint SectorSizeInBytes { get; init; }
    public required ulong MaxPayloadSizeToTargetInBytes { get; init; }
    public required ulong MaxPayloadSizeToTargetInBytesSupported { get; init; }
    public required ulong MaxPayloadSizeFromTargetInBytes { get; init; }
    public required ulong MaxXmlSizeInBytes { get; init; }
    public required ulong MaxDigestTableSizeInBytes { get; init; }
    public required bool Verbose { get; init; }
    public required bool AlwaysValidate { get; init; }
    public required bool ZlpAwareHost { get; init; }
    public required bool SkipWrite { get; init; }
    public required bool SkipStorageInit { get; init; }
    public required bool ManualStorage { get; init; }
    public required bool AuthenticationCompleted { get; init; }
    public string? TargetName { get; init; }
    public ulong Version { get; init; }
    public ulong MinVersionSupported { get; init; }

    public static ConfigureState Create(FirehoseConfiguration configuration) => new()
    {
        Storage = configuration.MemoryName == FirehoseStorage.None
            ? FirehoseStorage.Emmc
            : configuration.MemoryName,
        SectorSizeInBytes = configuration.MemoryName == FirehoseStorage.None
            ? 512
            : configuration.SectorSizeInBytes,
        MaxPayloadSizeToTargetInBytes = configuration.MaxPayloadSizeToTargetInBytes,
        MaxPayloadSizeToTargetInBytesSupported = configuration.MaxPayloadSizeToTargetInBytes,
        MaxPayloadSizeFromTargetInBytes = FirehoseConstants.DefaultMaxDigestTableSize,
        MaxXmlSizeInBytes = FirehoseConstants.InitialXmlBufferSize,
        MaxDigestTableSizeInBytes = configuration.MaxDigestTableSizeInBytes,
        Verbose = configuration.Verbose,
        AlwaysValidate = configuration.AlwaysValidate,
        ZlpAwareHost = configuration.ZlpAwareHost,
        SkipWrite = configuration.SkipWrite,
        SkipStorageInit = configuration.SkipStorageInit,
        ManualStorage = configuration.MemoryName != FirehoseStorage.None,
        AuthenticationCompleted = false
    };

    public ConfigureCommand CreateCommand() => new()
    {
        MemoryName = Storage,
        Verbose = Verbose ? (byte)1 : (byte)0,
        AlwaysValidate = AlwaysValidate ? (byte)1 : (byte)0,
        MaxDigestTableSizeInBytes = MaxDigestTableSizeInBytes,
        MaxPayloadSizeToTargetInBytes = MaxPayloadSizeToTargetInBytes,
        ZlpAwareHost = ZlpAwareHost ? (byte)1 : (byte)0,
        SkipWrite = SkipWrite ? (byte)1 : null,
        SkipStorageInit = SkipStorageInit ? (byte)1 : (byte)0
    };

    public ConfigureState Apply(ConfigureEvidence evidence)
    {
        FirehoseStorage storage = evidence.Storage ?? Storage;
        uint sectorSize = evidence.SectorSizeInBytes ??
                          (storage == Storage ? SectorSizeInBytes : DefaultSectorSize(storage));
        ulong supported = evidence.MaxPayloadSizeToTargetInBytesSupported ??
                          MaxPayloadSizeToTargetInBytesSupported;
        ulong payload = MinPositive(
            MaxPayloadSizeToTargetInBytes,
            evidence.MaxPayloadSizeToTargetInBytes,
            evidence.MaxPayloadSizeToTargetInBytesSupported);

        return this with
        {
            Storage = storage,
            SectorSizeInBytes = sectorSize,
            MaxPayloadSizeToTargetInBytes = payload,
            MaxPayloadSizeToTargetInBytesSupported = supported,
            MaxPayloadSizeFromTargetInBytes = evidence.MaxPayloadSizeFromTargetInBytes ??
                                              MaxPayloadSizeFromTargetInBytes,
            MaxXmlSizeInBytes = evidence.MaxXmlSizeInBytes ?? MaxXmlSizeInBytes,
            MaxDigestTableSizeInBytes = evidence.MaxDigestTableSizeInBytes is { } digest
                ? Math.Min(MaxDigestTableSizeInBytes, digest)
                : MaxDigestTableSizeInBytes,
            TargetName = NormalizeTargetName(evidence.TargetName) ?? TargetName,
            Version = evidence.Version ?? Version,
            MinVersionSupported = evidence.MinVersionSupported ?? MinVersionSupported
        };
    }

    public ConfigureState WithStorage(FirehoseStorage storage) => this with
    {
        Storage = storage,
        SectorSizeInBytes = DefaultSectorSize(storage)
    };

    public FirehoseConfigureResponse ToResponse(int attempts, FirehoseCommandResult result) => new()
    {
        Storage = Storage,
        SectorSizeInBytes = SectorSizeInBytes,
        Attempts = attempts,
        MemoryName = Storage.ToWireString(),
        TargetName = TargetName ?? "Unknown",
        MinVersionSupported = MinVersionSupported,
        Version = Version,
        MaxPayloadSizeToTargetInBytes = MaxPayloadSizeToTargetInBytes,
        MaxPayloadSizeToTargetInBytesSupported = MaxPayloadSizeToTargetInBytesSupported,
        MaxPayloadSizeFromTargetInBytes = MaxPayloadSizeFromTargetInBytes,
        MaxXmlSizeInBytes = MaxXmlSizeInBytes,
        MaxDigestTableSizeInBytes = MaxDigestTableSizeInBytes,
        Attributes = result.Attributes
    };

    public static uint DefaultSectorSize(FirehoseStorage storage) => storage switch
    {
        FirehoseStorage.Emmc or FirehoseStorage.Nvme => 512,
        _ => 4096
    };

    private static ulong MinPositive(ulong current, ulong? first, ulong? second)
    {
        ulong result = current;
        if (first is > 0)
            result = Math.Min(result, first.Value);
        if (second is > 0)
            result = Math.Min(result, second.Value);
        return result;
    }

    private static string? NormalizeTargetName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string target = value.Trim();
        return target.Contains("MSM", StringComparison.OrdinalIgnoreCase) ? target : $"MSM{target}";
    }
}

internal sealed record ConfigureEvidence
{
    public FirehoseStorage? Storage { get; init; }
    public FirehoseStorage? UnsupportedStorage { get; init; }
    public uint? SectorSizeInBytes { get; init; }
    public ulong? MaxPayloadSizeToTargetInBytes { get; init; }
    public ulong? MaxPayloadSizeToTargetInBytesSupported { get; init; }
    public ulong? MaxPayloadSizeFromTargetInBytes { get; init; }
    public ulong? MaxXmlSizeInBytes { get; init; }
    public ulong? MaxDigestTableSizeInBytes { get; init; }
    public string? TargetName { get; init; }
    public ulong? Version { get; init; }
    public ulong? MinVersionSupported { get; init; }
}
