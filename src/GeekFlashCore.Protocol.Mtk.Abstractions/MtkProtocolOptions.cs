namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Finite host limits. Device-supplied sizes must fit these limits before allocation.</summary>
public sealed record MtkProtocolOptions
{
    /// <summary>Default finite budget for the entire MediaTek connection, including providers and DA upload.</summary>
    public const int DefaultConnectTimeoutMilliseconds = 1000 * 60 * 3;
    /// <summary>Finite XFlash advertised packet ceiling, including observed 2 MiB DA2 capabilities.</summary>
    public const int MaximumXFlashPacketLength = 2 * 1024 * 1024;
    public int ReadTimeoutMilliseconds { get; init; } = 3000;
    public int ConnectTimeoutMilliseconds { get; init; } = DefaultConnectTimeoutMilliseconds;
    public int OperationTimeoutMilliseconds { get; init; } = 120000;
    public int ResourceTimeoutMilliseconds { get; init; } = 1000 * 60 * 1;
    public int BufferSize { get; init; } = 65536;
    /// <summary>BROM upload host chunk size, not USB max packet size. Zero uses BufferSize.
    /// Set 64 together with BromUploadZeroLengthPacket to recover the legacy host write shape.</summary>
    public int BromUploadChunkSize { get; init; }
    /// <summary>Explicit compatibility tail ZLP after a BROM DA/certificate/authentication upload.
    /// Default continuous bulk writes follow Penumbra without an additional ZLP.</summary>
    public bool BromUploadZeroLengthPacket { get; init; }
    public int MaximumFrameSize { get; init; } = 1048576;
    /// <summary>Maximum XFlash storage-data FLOW length, streamed through BufferSize windows.
    /// Independent of MaximumFrameSize for control, authentication, messages and scoped channel frames.
    /// Defaults to 2 MiB; an explicit smaller limit rejects larger data frames without retry.</summary>
    public int MaximumXFlashDataFrameSize { get; init; } = MaximumXFlashPacketLength;
    public int MaximumXmlSize { get; init; } = 65536;
    public int MaximumMessages { get; init; } = 128;
    public int MaximumProgressEvents { get; init; } = 4096;
    /// <summary>Maximum discarded startup bytes before the first handshake response.
    /// The default admits repeated Preloader READY messages within the same finite handshake budget.</summary>
    public int MaximumHandshakePrefix { get; init; } = 64;
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
    /// <summary>Explicitly enables logical NAND data-page writes with ECC. OOB/physical writes are excluded.</summary>
    public bool EnableNandLogicalWrites { get; init; }
    /// <summary>Host-confirmed usable NAND capacity for Legacy/XML which report only total capacity.</summary>
    public long? NandLogicalCapacity { get; init; }
    /// <summary>Uses the documented three-region MT6261 IoT DA boot sequence. No automatic dialect guessing.</summary>
    public bool LegacyIoT { get; init; }
    /// <summary>Confirmed NOR erase alignment. Zero leaves erase unavailable; no erase geometry is guessed.</summary>
    public int NorEraseBlockSize { get; init; }
    /// <summary>Explicit Legacy PMT layout when USER has no GPT. Null uses DiskV1 on 512-byte eMMC only.</summary>
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
        if (NandLogicalCapacity is <= 0 || LegacyIoT && DaKind is { } da && da != MtkDaKind.Legacy || NorEraseBlockSize < 0 || NorEraseBlockSize > 16777216 ||
            NorEraseBlockSize != 0 && (NorEraseBlockSize & (NorEraseBlockSize - 1)) != 0 ||
            LegacyPmtLayout is { } pmt && !Enum.IsDefined(pmt))
            throw new ArgumentOutOfRangeException(nameof(NorEraseBlockSize));
        if (BromUploadChunkSize != 0 && (BromUploadChunkSize is < 64 or > 1048576))
            throw new ArgumentOutOfRangeException(nameof(BromUploadChunkSize));
        if (MaximumXFlashDataFrameSize is < 512 or > MaximumXFlashPacketLength ||
            BufferSize is < 512 or > 1048576 || MaximumFrameSize < BufferSize || MaximumFrameSize > 1048576 ||
            MaximumXmlSize is < 1024 or > 65536 || MaximumXmlSize > MaximumFrameSize || MaximumMessages is < 1 or > 1024 ||
            MaximumProgressEvents is < 1 or > 65536 || MaximumHandshakePrefix is < 0 or > 1024 ||
            DaKind is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(BufferSize));
    }
}
/// <summary>Explicit chip-specific access policy. Unknown chips receive no guessed addresses.</summary>
public sealed record MtkChipProfile(ushort HardwareCode, uint WatchdogAddress, uint WatchdogValue,
    uint SejBase = 0, uint TzccBase = 0, int WatchdogWidth = 32);
