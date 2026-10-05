namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Finite host limits. Device-supplied sizes must fit these limits before allocation.</summary>
public sealed record MtkProtocolOptions
{
    /// <summary>Default finite budget for the entire MediaTek connection, including providers and DA upload.</summary>
    public const int DefaultConnectTimeoutMilliseconds = 60000;
    public int ReadTimeoutMilliseconds { get; init; } = 3000;
    public int ConnectTimeoutMilliseconds { get; init; } = DefaultConnectTimeoutMilliseconds;
    public int OperationTimeoutMilliseconds { get; init; } = 120000;
    public int ResourceTimeoutMilliseconds { get; init; } = 30000;
    public int BufferSize { get; init; } = 65536;
    public int MaximumFrameSize { get; init; } = 1048576;
    public int MaximumXmlSize { get; init; } = 65536;
    public int MaximumMessages { get; init; } = 128;
    public int MaximumProgressEvents { get; init; } = 4096;
    public int MaximumHandshakePrefix { get; init; } = 4;
    public MtkDaKind? DaKind
    {
        get; init;
    }
    public ushort? DaHardwareCode
    {
        get; init;
    }
    /// <summary>Initializes an explicitly configured or known watchdog immediately after FD during Probe.
    /// Default Probe only queries; Connect prepares it before security queries and resource requests.</summary>
    public bool InitializeWatchdogOnProbe { get; init; }
    /// <summary>Explicitly enables XFlash logical NAND data-page writes with ECC. OOB/physical writes are excluded.</summary>
    public bool EnableNandLogicalWrites { get; init; }
    /// <summary>Confirmed NOR erase alignment. Zero leaves erase unavailable; no erase geometry is guessed.</summary>
    public int NorEraseBlockSize { get; init; }
    /// <summary>Confirmed Legacy PMT layout used when a user region contains no GPT.</summary>
    public MtkPmtLayout? LegacyPmtLayout { get; init; }
    /// <summary>Explicit watchdog profile overriding known metadata. Unknown chips without a profile receive no writes.</summary>
    public MtkChipProfile? ChipProfile
    {
        get; init;
    }
    public void Validate()
    {
        if (ReadTimeoutMilliseconds <= 0 || ConnectTimeoutMilliseconds <= 0 ||
            OperationTimeoutMilliseconds <= 0 || ResourceTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(ReadTimeoutMilliseconds));
        if (NorEraseBlockSize < 0 || NorEraseBlockSize > 16777216 ||
            NorEraseBlockSize != 0 && (NorEraseBlockSize & (NorEraseBlockSize - 1)) != 0 ||
            LegacyPmtLayout is { } pmt && !Enum.IsDefined(pmt))
            throw new ArgumentOutOfRangeException(nameof(NorEraseBlockSize));
        if (BufferSize is < 512 or > 1048576 || MaximumFrameSize < BufferSize || MaximumFrameSize > 1048576 ||
            MaximumXmlSize is < 1024 or > 65536 || MaximumXmlSize > MaximumFrameSize || MaximumMessages is < 1 or > 1024 ||
            MaximumProgressEvents is < 1 or > 65536 || MaximumHandshakePrefix is < 0 or > 16 ||
            DaKind is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(BufferSize));
    }
}
/// <summary>Explicit chip-specific access policy. Unknown chips receive no guessed addresses.</summary>
public sealed record MtkChipProfile(ushort HardwareCode, uint WatchdogAddress, uint WatchdogValue,
    uint SejBase = 0, uint TzccBase = 0, int WatchdogWidth = 32);
