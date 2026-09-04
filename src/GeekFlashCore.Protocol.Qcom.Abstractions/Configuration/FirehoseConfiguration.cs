namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record FirehoseConfiguration
{
    public FirehoseStorage MemoryName { get; init; } = FirehoseStorage.None;
    public uint SectorSizeInBytes { get; init; } = 512;
    public ulong MaxPayloadSizeToTargetInBytes { get; init; } = FirehoseConstants.DefaultPayloadSize;
    public ulong MaxDigestTableSizeInBytes { get; init; } = FirehoseConstants.DefaultMaxDigestTableSize;
    public int MaxConfigureAttempts { get; init; } = FirehoseConstants.MaxConfigureAttempts;
    public bool Verbose { get; init; }
    public bool AlwaysValidate { get; init; }
    public bool ZlpAwareHost { get; init; } = true;
    public bool SkipWrite { get; init; }
    public bool SkipStorageInit { get; init; }
    public bool SkipResponse { get; init; }

    public void Validate()
    {
        if (SectorSizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(SectorSizeInBytes));
        if (MaxPayloadSizeToTargetInBytes is 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadSizeToTargetInBytes));
        if (MaxDigestTableSizeInBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxDigestTableSizeInBytes));
        if (MaxConfigureAttempts is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(MaxConfigureAttempts));
    }
}

public sealed record OplusDigestConfiguration
{
    public OplusDigestMode Mode { get; init; }
    public int FixedSectorCount { get; init; }
    public int MaxCommandsBeforeDigest { get; init; }

    public void Validate()
    {
        if (Mode != OplusDigestMode.OplusDigestLegacy)
            return;
        if (FixedSectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(FixedSectorCount));
        if (MaxCommandsBeforeDigest < 2)
            throw new ArgumentOutOfRangeException(nameof(MaxCommandsBeforeDigest));
    }
}
