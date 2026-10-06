namespace GeekFlashCore.Protocol.Qcom.Abstractions;

/// <summary>The reason an XML entry was not sent to the device.</summary>
public enum FirehoseScriptSkipReason
{
    UnsupportedCommand,
    DeviceCommandUnavailable,
    EmptyFileName,
    NonDiskPatch
}

/// <summary>A skipped entry, identified by its one-based position and command name.</summary>
public sealed record FirehoseScriptSkippedEntry(int Index, string Command, FirehoseScriptSkipReason Reason)
{
    /// <summary>The partition label from the XML, if present.</summary>
    public string? Label { get; init; }
    /// <summary>The filename from the XML, including an empty filename.</summary>
    public string? FileName { get; init; }
    /// <summary>The original LUN expression, if specified.</summary>
    public string? PhysicalPartitionExpression { get; init; }
    /// <summary>The LUN when it can be resolved without device I/O.</summary>
    public uint? PhysicalPartitionNumber { get; init; }
    /// <summary>The original start sector expression.</summary>
    public string? StartSectorExpression { get; init; }
    /// <summary>The start sector when it can be resolved using cached geometry.</summary>
    public long? StartSector { get; init; }
    /// <summary>The original sector count expression. Zero retains its XML meaning.</summary>
    public string? SectorCountExpression { get; init; }
    /// <summary>The sector count when the XML expression can be resolved.</summary>
    public long? SectorCount { get; init; }
}

/// <summary>Successful script execution totals. Earlier writes cannot be rolled back on failure.</summary>
public sealed record FirehoseScriptResult(int ExecutedCommands, long BytesWritten,
    IReadOnlyList<FirehoseScriptSkippedEntry> SkippedEntries);
