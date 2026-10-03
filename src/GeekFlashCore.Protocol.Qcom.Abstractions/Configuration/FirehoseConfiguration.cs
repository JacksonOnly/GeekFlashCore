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
    /// <summary>Selects partition mapping or the Rector packet-counted Legacy flow.</summary>
    public OplusDigestMode Mode { get; init; }
    /// <summary>Maximum sectors per transfer; zero keeps the request unsegmented.</summary>
    public int FixedSectorCount { get; init; }
    /// <summary>Legacy table packet capacity. Counts XML and complete outgoing payloads, not transport chunks.</summary>
    public int MaxCommandsBeforeDigest { get; init; } = 53;
    /// <summary>Packet count supplied by a host resuming its own table state.</summary>
    public uint InitialPacketCount { get; init; }
    /// <summary>Exact confirmation NOP; null selects the compatible built-in NOP.</summary>
    public string? NopXml { get; init; }
    /// <summary>Legacy wire XML truncation limit retained from Rector.</summary>
    public int MaximumXmlSendSize { get; init; } = 4096;
    /// <summary>Independent Digest reply window, including partial XML.</summary>
    public int DigestResponseTimeoutMilliseconds { get; init; } = 1000;

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (Mode != OplusDigestMode.OplusDigestLegacy)
            return;
        if (FixedSectorCount < 0)
            throw new ArgumentOutOfRangeException(nameof(FixedSectorCount));
        if (MaxCommandsBeforeDigest < 4)
            throw new ArgumentOutOfRangeException(nameof(MaxCommandsBeforeDigest));
        if (InitialPacketCount > (long)MaxCommandsBeforeDigest + 1)
            throw new ArgumentOutOfRangeException(nameof(InitialPacketCount));
        if (MaximumXmlSendSize is < 1 or > FirehoseConstants.MaximumXmlPacketSize)
            throw new ArgumentOutOfRangeException(nameof(MaximumXmlSendSize));
        if (DigestResponseTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(DigestResponseTimeoutMilliseconds));
        if (NopXml?.Length > FirehoseConstants.MaximumXmlPacketSize)
            throw new ArgumentOutOfRangeException(nameof(NopXml));
    }
}
