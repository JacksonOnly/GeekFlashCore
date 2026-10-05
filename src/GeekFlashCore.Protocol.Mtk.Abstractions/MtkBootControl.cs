namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Observed boot-control slot metadata, not a claim that Android has booted successfully.</summary>
public sealed record MtkBootSlot(int Index, int Priority, int TriesRemaining, bool SuccessfulBoot, bool VerityCorrupted);
/// <summary>Validated Android boot-control metadata from misc/para at byte offset 0x800.</summary>
public sealed record MtkBootControlInfo(int Version, int ActiveSlot, IReadOnlyList<MtkBootSlot> Slots)
{
    /// <summary>Current suffix recorded by the bootloader, which can differ from the highest-priority active slot.</summary>
    public int CurrentSlot { get; init; } = ActiveSlot;
}
/// <summary>A boot-control write or its readback failed after writing could have started.</summary>
public sealed class MtkBootControlWriteException(Exception inner) :
    GeekFlashCore.Protocol.Abstractions.ProtocolException(Localization.Strings.BootControlWriteUnknown, inner)
{
    /// <summary>The host must reconnect and inspect the device; automatic retry is prohibited.</summary>
    public bool MayHaveWritten => true;
}
