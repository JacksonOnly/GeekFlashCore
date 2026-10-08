using GeekFlashCore.Protocol.Sprd.Abstractions.Localization;

namespace GeekFlashCore.Protocol.Sprd.Abstractions;

/// <summary>Immutable connection profile. The protocol snapshots partition lists at construction.</summary>
public sealed record SprdProtocolOptions
{
    /// <summary>Initial device stage; automatic stage guessing is not performed.</summary>
    public SprdBootStage EntryStage { get; init; } = SprdBootStage.BootRom;
    /// <summary>Maximum synchronous response time for one command.</summary>
    public int CommandTimeoutMilliseconds { get; init; } = 10_000;
    /// <summary>Total connection budget including asynchronous resource acquisition.</summary>
    public int ConnectTimeoutMilliseconds { get; init; } = 120_000;
    /// <summary>Total budget for one storage operation.</summary>
    public int OperationTimeoutMilliseconds { get; init; } = 600_000;
    /// <summary>Maximum asynchronous loader-provider wait.</summary>
    public int ResourceRequestTimeoutMilliseconds { get; init; } = 30_000;
    /// <summary>Boot ROM upload payload size.</summary>
    public int BootRomBlockSize { get; init; } = 528;
    /// <summary>FDL upload and storage payload size.</summary>
    public int TransferBlockSize { get; init; } = 4096;
    /// <summary>Per-image loader limit; this does not allocate a buffer of that size.</summary>
    public int MaximumLoaderBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Largest accepted response payload, at most 65535 bytes.</summary>
    public int MaximumResponseBytes { get; init; } = ushort.MaxValue;
    /// <summary>Maximum native partition records.</summary>
    public int MaximumPartitions { get; init; } = 512;
    /// <summary>Maximum unsolicited log frames skipped within one command.</summary>
    public int MaximumLogFrames { get; init; } = 32;
    /// <summary>Pad outgoing odd-length payloads with zero, as in the SPD tool profile.</summary>
    public bool PadOddPayloads { get; init; }
    /// <summary>Send KEEP_CHARGE after connecting to FDL1.</summary>
    public bool KeepCharge { get; init; } = true;
    /// <summary>Explicitly negotiate unescaped frames after FDL2 starts.</summary>
    public bool DisableTranscode { get; init; }
    /// <summary>Frames at an explicitly selected FDL2 entry already have escaping disabled.</summary>
    public bool EntryTranscodeDisabled { get; init; }
    /// <summary>Selector/offset profile; 64-bit modes are never selected implicitly.</summary>
    public SprdPartitionLengthEncoding PartitionLengthEncoding { get; init; }
    /// <summary>Explicit native READ_PARTITION size-unit multiplier in bytes. Null requires a supplied list or GPT profile.</summary>
    public long? PartitionTableSizeUnitBytes { get; init; }
    /// <summary>Capacity source when KnownPartitions is empty. No fallback is performed.</summary>
    public SprdPartitionTableSource PartitionTableSource { get; init; }
    /// <summary>Confirmed GPT logical sector size, 512 or 4096; required for UserPartitionGpt.</summary>
    public int? GptSectorSize { get; init; }
    /// <summary>Confirmed user_partition prefix window, sector aligned and at most 4 MiB.</summary>
    public int GptReadBytes { get; init; } = 32 * 1024;
    /// <summary>Explicit raw download mode; never inferred from loader capabilities.</summary>
    public SprdRawDataMode RawDataMode { get; init; }
    /// <summary>Confirmed raw flush window in bytes, required when raw mode is enabled, at most 4 MiB.</summary>
    public int? RawDataFlushSizeBytes { get; init; }
    /// <summary>Confirmed USB bulk OUT packet size (8 through 1024, power of two), required for raw USB.</summary>
    public int? RawDataUsbPacketSize { get; init; }
    /// <summary>Optional host-verified capacities, for example from an independently validated PAC manifest.</summary>
    public IReadOnlyList<SprdPartition> KnownPartitions { get; init; } = [];

    /// <summary>Validates finite budgets, frame limits and confirmed partition capacities.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(EntryStage) || !Enum.IsDefined(PartitionLengthEncoding) ||
            !Enum.IsDefined(PartitionTableSource) || !Enum.IsDefined(RawDataMode) ||
            CommandTimeoutMilliseconds <= 0 || ConnectTimeoutMilliseconds <= 0 || OperationTimeoutMilliseconds <= 0 ||
            ResourceRequestTimeoutMilliseconds <= 0 || BootRomBlockSize is < 1 or > 65534 ||
            TransferBlockSize is < 1 or > 65534 || MaximumLoaderBytes is < 1 or > 256 * 1024 * 1024 ||
            MaximumResponseBytes is < 76 or > 65535 || TransferBlockSize > MaximumResponseBytes ||
            MaximumPartitions is < 1 or > 862 || MaximumLogFrames is < 0 or > 1024 ||
            PartitionTableSizeUnitBytes is <= 0 or > 1024 * 1024 * 1024 ||
            EntryTranscodeDisabled && EntryStage != SprdBootStage.Fdl2 ||
            PadOddPayloads && (BootRomBlockSize % 2 != 0 || TransferBlockSize % 2 != 0) ||
            KnownPartitions is null || KnownPartitions.Count > MaximumPartitions)
            throw new ArgumentException(Strings.InvalidOptions);
        if (GptSectorSize is not (null or 512 or 4096) || GptReadBytes is < 1024 or > 4 * 1024 * 1024 ||
            PartitionTableSource == SprdPartitionTableSource.UserPartitionGpt &&
                (GptSectorSize is null || GptReadBytes % GptSectorSize.Value != 0 || GptReadBytes < 3 * GptSectorSize.Value) ||
            RawDataFlushSizeBytes is <= 0 or > 4 * 1024 * 1024 ||
            RawDataMode != SprdRawDataMode.Disabled && RawDataFlushSizeBytes is null ||
            RawDataMode == SprdRawDataMode.Disabled && (RawDataFlushSizeBytes is not null || RawDataUsbPacketSize is not null) ||
            RawDataUsbPacketSize is int packet && (packet is < 8 or > 1024 || (packet & (packet - 1)) != 0))
            throw new ArgumentException(Strings.InvalidOptions);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var partition in KnownPartitions)
        {
            if (partition is null) throw new ArgumentException(Strings.InvalidOptions);
            ValidatePartitionName(partition.Name);
            if (partition.Length <= 0 || !names.Add(partition.Name)) throw new ArgumentException(Strings.InvalidPartitions);
        }
    }

    /// <summary>Checks that a name fits the NUL-terminated 36-code-unit BSL field.</summary>
    public static void ValidatePartitionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 35 || name.Any(c => char.IsControl(c) || char.IsSurrogate(c)))
            throw new ArgumentException(Strings.InvalidPartitionName, nameof(name));
    }
}
