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
public sealed record FirehoseScriptSkippedEntry(int Index, string Command, FirehoseScriptSkipReason Reason);

/// <summary>Successful script execution totals. Earlier writes cannot be rolled back on failure.</summary>
public sealed record FirehoseScriptResult(int ExecutedCommands, long BytesWritten,
    IReadOnlyList<FirehoseScriptSkippedEntry> SkippedEntries);
