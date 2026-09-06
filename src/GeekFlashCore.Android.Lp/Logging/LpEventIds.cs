using Microsoft.Extensions.Logging;

namespace GeekFlashCore.Android.Lp;

internal static class LpEventIds
{
    internal static readonly EventId CommitStarted = new(2400, nameof(CommitStarted));
    internal static readonly EventId DataPhaseCompleted = new(2401, nameof(DataPhaseCompleted));
    internal static readonly EventId OldCopyRepaired = new(2402, nameof(OldCopyRepaired));
    internal static readonly EventId MetadataCommitted = new(2403, nameof(MetadataCommitted));
    internal static readonly EventId VerificationIncomplete = new(2404, nameof(VerificationIncomplete));
    internal static readonly EventId CleanupIncomplete = new(2405, nameof(CleanupIncomplete));
    internal static readonly EventId FileOpened = new(2406, nameof(FileOpened));
    internal static readonly EventId RawExportCompleted = new(2407, nameof(RawExportCompleted));
    internal static readonly EventId LeaseDisposeFailed = new(2408, nameof(LeaseDisposeFailed));
}
