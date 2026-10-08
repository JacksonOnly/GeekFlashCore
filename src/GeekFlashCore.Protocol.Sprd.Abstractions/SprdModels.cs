namespace GeekFlashCore.Protocol.Sprd.Abstractions;

/// <summary>The selected entry profile or observed loader stage.</summary>
public enum SprdBootStage
{
    /// <summary>Boot ROM; both FDL images are required.</summary>
    BootRom,
    /// <summary>Already running FDL1; only FDL2 is required.</summary>
    Fdl1,
    /// <summary>Already running FDL2; no image is uploaded.</summary>
    Fdl2,
    /// <summary>Detect the initial stage from validated handshake responses before selecting loaders.</summary>
    Auto
}

/// <summary>Lifecycle of one serialized connection.</summary>
public enum SprdSessionState
{
    /// <summary>Ready to open a new connection.</summary>
    Disconnected,
    /// <summary>Resolving resources or handshaking.</summary>
    Connecting,
    /// <summary>Boot ROM connected.</summary>
    BootRom,
    /// <summary>FDL1 connected.</summary>
    Fdl1,
    /// <summary>FDL2 storage commands are available.</summary>
    StorageReady,
    /// <summary>The connection must be disconnected before reuse.</summary>
    Faulted,
    /// <summary>The instance has been disposed.</summary>
    Disposed
}

/// <summary>Explicit device profile for the partition selector and read offset width.</summary>
public enum SprdPartitionLengthEncoding
{
    /// <summary>72-byte name followed by a 32-bit length.</summary>
    UInt32,
    /// <summary>72-byte name followed by a 64-bit length (Xia layout).</summary>
    UInt64,
    /// <summary>72-byte name, 64-bit length, eight reserved zero bytes (SPD tool layout).</summary>
    UInt64WithReserved
}

/// <summary>Explicit source for named-partition capacities when no host list is supplied.</summary>
public enum SprdPartitionTableSource
{
    /// <summary>READ_PARTITION records scaled by a confirmed unit.</summary>
    Native,
    /// <summary>Strict primary GPT read from a bounded user_partition window.</summary>
    UserPartitionGpt
}

/// <summary>Confirmed FDL2 download profile. Loader uploads always use framed MIDST.</summary>
public enum SprdRawDataMode
{
    /// <summary>Ordinary framed MIDST with one ACK per frame.</summary>
    Disabled,
    /// <summary>One offset/length command followed by raw bytes and an ACK per flush window.</summary>
    Version1,
    /// <summary>One raw-start command followed by raw flush windows and an ACK per window.</summary>
    Version2
}

/// <summary>A verified named-partition capacity in bytes, without an inferred disk offset.</summary>
public sealed record SprdPartition(string Name, long Length);

/// <summary>Validated loader capability metadata; it is not an authentication result.</summary>
public sealed record SprdLoaderInfo(uint Version, bool SupportsDisableTranscode, byte OldMemoryFlags,
    byte RawDataSupport, uint FlushSizeKiB, uint StorageType);

/// <summary>Connection metadata. Version text is bounded and is never automatically logged.</summary>
public sealed record SprdTargetInfo(SprdBootStage Stage, string? Version, SprdLoaderInfo? Loader)
{
    /// <summary>Initial stage used by the successful connection, before any loader upload.</summary>
    public SprdBootStage? EntryStage { get; init; }
    /// <summary>Logical sector size confirmed by a successful GPT query; null until queried or for host/native lists.</summary>
    public int? GptSectorSize { get; init; }
}
