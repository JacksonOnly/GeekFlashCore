namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Passive startup evidence, not a complete identity or dialect proof.</summary>
public enum MtkEntrySignal { None, Da1Sync, FramedDa, Other }

/// <summary>Attaches to an already running DA without replaying BROM commands.</summary>
public interface IMtkDaReconnect
{
    /// <summary>Reads at most one available startup byte without writing. The byte is retained for the selected protocol.</summary>
    MtkEntrySignal InspectEntrySignal(CancellationToken cancellationToken = default);
    /// <summary>The host must confirm the physical device, dialect and DA1/DA2 boundary.
    /// Sources are borrowed. Legacy DA2 additionally requires previously observed geometry;
    /// no storage capacity is inferred. A failed wire validation invalidates this session.</summary>
    void ResumeDownloadAgent(MtkBootStage stage, MtkTargetInfo target, MtkConnectionResources resources,
        MtkStorageInfo? legacyStorage = null, CancellationToken cancellationToken = default);
}
