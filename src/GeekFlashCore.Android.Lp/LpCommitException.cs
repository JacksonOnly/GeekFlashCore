using GeekFlashCore.Android.Lp.Abstractions;

namespace GeekFlashCore.Android.Lp;

public sealed class LpCommitException : Exception
{
    public LpCommitException(LpCommitFailure failure)
        : base(GetMessage(failure), failure?.InnerException)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    public LpCommitFailure Failure { get; }

    private static string GetMessage(LpCommitFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.ErrorCode switch
        {
            LpCommitErrorCode.InvalidVerificationOptions => Resources.InvalidVerificationOptions,
            LpCommitErrorCode.ResolverFailed => Resources.ResolverFailed,
            LpCommitErrorCode.DeviceIdentityMismatch => Resources.DeviceIdentityMismatch,
            LpCommitErrorCode.AliasedBlockDevice => Resources.AliasedBlockDevice,
            LpCommitErrorCode.SourceChangedBeforeWrite => Resources.SourceChangedBeforeWrite,
            LpCommitErrorCode.SourceChangedAfterDataWrite => Resources.SourceChangedAfterDataWrite,
            LpCommitErrorCode.CancelledAfterDataWrite => Resources.CancelledAfterDataWrite,
            LpCommitErrorCode.DataWriteFailed => Resources.DataWriteFailed,
            LpCommitErrorCode.DataFlushFailed => Resources.DataFlushFailed,
            LpCommitErrorCode.MetadataReadFailed => Resources.MetadataReadFailed,
            LpCommitErrorCode.OldMetadataRepairFailed => Resources.OldMetadataRepairFailed,
            LpCommitErrorCode.PrimaryMetadataWriteFailed => Resources.PrimaryMetadataWriteFailed,
            LpCommitErrorCode.PrimaryMetadataFlushFailed => Resources.PrimaryMetadataFlushFailed,
            LpCommitErrorCode.BackupMetadataWriteFailed => Resources.BackupMetadataWriteFailed,
            LpCommitErrorCode.BackupMetadataFlushFailed => Resources.BackupMetadataFlushFailed,
            LpCommitErrorCode.MetadataVerificationFailed => Resources.MetadataVerificationFailed,
            LpCommitErrorCode.PayloadVerificationFailed => Resources.PayloadVerificationFailed,
            LpCommitErrorCode.VerificationCancelled => Resources.VerificationCancelled,
            LpCommitErrorCode.CleanupFailed => Resources.CleanupFailed,
            LpCommitErrorCode.CleanupCancelled => Resources.CleanupCancelled,
            _ => failure.ResourceKey
        };
    }
}
