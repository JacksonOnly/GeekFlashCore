using System.Runtime.CompilerServices;
using GeekFlashCore.ImageFormats.Abstractions;
using GeekFlashCore.BlockDevice.Abstractions;

[assembly: InternalsVisibleTo("GeekFlashCore.Android.Lp")]
[assembly: InternalsVisibleTo("GeekFlashCore.Android.Lp.Tests")]

namespace GeekFlashCore.Android.Lp.Abstractions;

public sealed record LpWriteLimits
{
    public const int DefaultDataBufferSize = 256 * 1024;
    public const int AbsoluteMaximumDataBufferSize = 16 * 1024 * 1024;

    public static LpWriteLimits Default { get; } = new();

    public int DataBufferSize { get; init; } = DefaultDataBufferSize;

    public void Validate()
    {
        if (DataBufferSize is < 1 or > AbsoluteMaximumDataBufferSize)
        {
            throw new ArgumentOutOfRangeException(nameof(DataBufferSize));
        }
    }
}

public sealed record LpEditOpenOptions
{
    public ImageReadLimits ReadLimits { get; init; } = ImageReadLimits.Default;
    public LpWriteLimits WriteLimits { get; init; } = LpWriteLimits.Default;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ReadLimits);
        ArgumentNullException.ThrowIfNull(WriteLimits);
        ReadLimits.Validate();
        WriteLimits.Validate();
    }
}

public sealed record LpPlanOptions
{
    public bool AllowInPlaceDataOverwrite { get; init; }
}

public sealed record LpCommitOptions
{
    public bool VerifyAfterCommit { get; init; } = true;
    public bool VerifyPayloadHash { get; init; }
    public bool WipeFreedExtents { get; init; }
}

public readonly record struct LpPartitionHandle
{
    public LpPartitionHandle(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    public int Value { get; }
    public bool IsEmpty => Value == 0;
}

public readonly record struct LpGroupHandle
{
    public LpGroupHandle(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    public int Value { get; }
    public bool IsEmpty => Value == 0;
}

public enum LpGroupRemovalMode
{
    FailIfNotEmpty = 0,
    RemovePartitions = 1
}

public enum LpEditErrorCode
{
    InvalidName = 1,
    DuplicateName = 2,
    SlotOutOfRange = 3,
    UnsupportedMetadataVersion = 4,
    UnsupportedMetadataFeature = 5,
    PartitionNotFound = 6,
    GroupNotFound = 7,
    GroupNotEmpty = 8,
    GroupCapacityExceeded = 9,
    MetadataTooLarge = 10,
    InsufficientSpace = 11,
    InPlaceOverwriteRequired = 12,
    ExtentConflict = 13,
    CrossSlotExtentConflict = 14,
    UnreadableNonTargetSlot = 15,
    AlignmentViolation = 16,
    SourceChanged = 17,
    InvalidSparseImage = 18
}

public enum LpCommitErrorCode
{
    InvalidVerificationOptions = 1,
    ResolverFailed = 2,
    DeviceIdentityMismatch = 3,
    AliasedBlockDevice = 4,
    SourceChangedBeforeWrite = 5,
    SourceChangedAfterDataWrite = 6,
    CancelledAfterDataWrite = 7,
    DataWriteFailed = 8,
    DataFlushFailed = 9,
    MetadataReadFailed = 10,
    OldMetadataRepairFailed = 11,
    PrimaryMetadataWriteFailed = 12,
    PrimaryMetadataFlushFailed = 13,
    BackupMetadataWriteFailed = 14,
    BackupMetadataFlushFailed = 15,
    MetadataVerificationFailed = 16,
    PayloadVerificationFailed = 17,
    VerificationCancelled = 18,
    CleanupFailed = 19,
    CleanupCancelled = 20
}

public enum DataRollbackState
{
    NotRequired = 0,
    OriginalDataPreserved = 1,
    Unavailable = 2
}

public enum CommitStatus
{
    NotCommitted = 0,
    Succeeded = 1
}

public enum VerificationStatus
{
    NotRequested = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3
}

public enum CleanupStatus
{
    NotRequested = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3
}

public enum MetadataCopyState
{
    Unknown = 0,
    OriginalValid = 1,
    OriginalInvalid = 2,
    Repaired = 3,
    NewWritten = 4,
    NewDurable = 5
}

public enum DurabilityLevel
{
    None = 0,
    WriteAccepted = 1,
    ProtocolAcknowledged = 2,
    FlushToDisk = 3
}

public enum LpCommitPhase
{
    NotStarted = 0,
    DevicesResolved = 1,
    DataWritten = 2,
    DataFlushed = 3,
    SourceRevalidated = 4,
    OldMetadataRepaired = 5,
    PrimaryMetadataWritten = 6,
    PrimaryMetadataFlushed = 7,
    BackupMetadataWritten = 8,
    BackupMetadataFlushed = 9,
    MetadataVerified = 10,
    PayloadVerified = 11,
    CleanupCompleted = 12
}

public sealed record LpDiagnosticContext
{
    public LpDiagnosticContext(
        int? slotNumber = null,
        LpMetadataCopyKind? metadataCopy = null,
        uint? blockDeviceIndex = null,
        BlockDeviceId? blockDeviceId = null,
        long? deviceRelativeOffset = null,
        long? length = null,
        string? partitionName = null,
        string? groupName = null,
        LpCommitPhase? phase = null)
    {
        if (slotNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotNumber));
        }

        if (deviceRelativeOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceRelativeOffset));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        SlotNumber = slotNumber;
        MetadataCopy = metadataCopy;
        BlockDeviceIndex = blockDeviceIndex;
        BlockDeviceId = blockDeviceId;
        DeviceRelativeOffset = deviceRelativeOffset;
        Length = length;
        PartitionName = partitionName;
        GroupName = groupName;
        Phase = phase;
    }

    public int? SlotNumber { get; }
    public LpMetadataCopyKind? MetadataCopy { get; }
    public uint? BlockDeviceIndex { get; }
    public BlockDeviceId? BlockDeviceId { get; }
    public long? DeviceRelativeOffset { get; }
    public long? Length { get; }
    public string? PartitionName { get; }
    public string? GroupName { get; }
    public LpCommitPhase? Phase { get; }
}

public sealed record LpEditFailure
{
    public LpEditFailure(
        LpEditErrorCode errorCode,
        string resourceKey,
        IReadOnlyList<object?>? resourceArguments = null,
        LpDiagnosticContext? diagnosticContext = null)
    {
        LpContractGuard.Validate(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);

        ErrorCode = errorCode;
        ResourceKey = resourceKey;
        ResourceArguments = LpContractGuard.CloneArguments(resourceArguments);
        DiagnosticContext = diagnosticContext;
    }

    public LpEditErrorCode ErrorCode { get; }
    public string ResourceKey { get; }
    public ReadOnlyMemory<object?> ResourceArguments { get; }
    public LpDiagnosticContext? DiagnosticContext { get; }
}

public sealed record LpCommitIssue
{
    public LpCommitIssue(
        LpCommitErrorCode errorCode,
        string resourceKey,
        IReadOnlyList<object?>? resourceArguments = null,
        LpDiagnosticContext? diagnosticContext = null,
        Exception? innerException = null)
    {
        LpContractGuard.Validate(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);

        ErrorCode = errorCode;
        ResourceKey = resourceKey;
        ResourceArguments = LpContractGuard.CloneArguments(resourceArguments);
        DiagnosticContext = diagnosticContext;
        InnerException = innerException;
    }

    public LpCommitErrorCode ErrorCode { get; }
    public string ResourceKey { get; }
    public ReadOnlyMemory<object?> ResourceArguments { get; }
    public LpDiagnosticContext? DiagnosticContext { get; }
    public Exception? InnerException { get; }
}

public sealed record LpCommitFailure
{
    public LpCommitFailure(
        LpCommitErrorCode errorCode,
        LpCommitPhase lastCompletedPhase,
        bool metadataChanged,
        DataRollbackState dataRollbackState,
        MetadataCopyState primaryCopyState,
        MetadataCopyState backupCopyState,
        DurabilityLevel durabilityLevel,
        string resourceKey,
        IReadOnlyList<object?>? resourceArguments = null,
        LpDiagnosticContext? diagnosticContext = null,
        Exception? innerException = null)
    {
        LpContractGuard.Validate(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);

        ErrorCode = errorCode;
        LastCompletedPhase = lastCompletedPhase;
        MetadataChanged = metadataChanged;
        DataRollbackState = dataRollbackState;
        PrimaryCopyState = primaryCopyState;
        BackupCopyState = backupCopyState;
        DurabilityLevel = durabilityLevel;
        ResourceKey = resourceKey;
        ResourceArguments = LpContractGuard.CloneArguments(resourceArguments);
        DiagnosticContext = diagnosticContext;
        InnerException = innerException;
    }

    public LpCommitErrorCode ErrorCode { get; }
    public LpCommitPhase LastCompletedPhase { get; }
    public bool MetadataChanged { get; }
    public DataRollbackState DataRollbackState { get; }
    public MetadataCopyState PrimaryCopyState { get; }
    public MetadataCopyState BackupCopyState { get; }
    public DurabilityLevel DurabilityLevel { get; }
    public string ResourceKey { get; }
    public ReadOnlyMemory<object?> ResourceArguments { get; }
    public LpDiagnosticContext? DiagnosticContext { get; }
    public Exception? InnerException { get; }
}

public sealed record LpCommitResult
{
    public LpCommitResult(
        CommitStatus commitStatus,
        VerificationStatus verificationStatus,
        CleanupStatus cleanupStatus,
        LpCommitPhase lastCompletedPhase,
        MetadataCopyState primaryCopyState,
        MetadataCopyState backupCopyState,
        DurabilityLevel durabilityLevel,
        LpCommitIssue? verificationIssue = null,
        LpCommitIssue? cleanupIssue = null)
    {
        LpContractGuard.ValidateIssue(verificationStatus, verificationIssue, nameof(verificationIssue));
        LpContractGuard.ValidateIssue(cleanupStatus, cleanupIssue, nameof(cleanupIssue));

        CommitStatus = commitStatus;
        VerificationStatus = verificationStatus;
        CleanupStatus = cleanupStatus;
        LastCompletedPhase = lastCompletedPhase;
        PrimaryCopyState = primaryCopyState;
        BackupCopyState = backupCopyState;
        DurabilityLevel = durabilityLevel;
        VerificationIssue = verificationIssue;
        CleanupIssue = cleanupIssue;
    }

    public CommitStatus CommitStatus { get; }
    public VerificationStatus VerificationStatus { get; }
    public CleanupStatus CleanupStatus { get; }
    public LpCommitPhase LastCompletedPhase { get; }
    public MetadataCopyState PrimaryCopyState { get; }
    public MetadataCopyState BackupCopyState { get; }
    public DurabilityLevel DurabilityLevel { get; }
    public LpCommitIssue? VerificationIssue { get; }
    public LpCommitIssue? CleanupIssue { get; }
    public bool CommitSucceeded => CommitStatus == CommitStatus.Succeeded;
}

public enum LpPlannedWriteKind
{
    Image = 0,
    Zero = 1
}

public readonly record struct LpPlannedDataWrite(
    LpPartitionHandle Partition,
    string PartitionName,
    LpPlannedWriteKind Kind,
    long SourceOffset,
    uint TargetSource,
    long TargetOffset,
    long Length);

public readonly record struct LpGroupUsage(
    string RawName,
    string Name,
    ulong MaximumSize,
    ulong UsedSize);

public sealed class LpCommitPlan
{
    internal LpCommitPlan(
        Guid sessionId,
        int slotNumber,
        DataRollbackState dataRollbackState,
        long requiredLogicalBytes = 0,
        long allocatedPhysicalBytes = 0,
        long allocatableBytes = 0,
        long remainingFreeBytes = 0,
        bool usesInPlaceDataOverwrite = false,
        LpPartition[]? partitions = null,
        LpExtent[]? extents = null,
        LpPartitionGroup[]? groups = null,
        LpBlockDevice[]? blockDevices = null,
        LpPlannedDataWrite[]? dataWrites = null,
        LpGroupUsage[]? groupUsages = null)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException(null, nameof(sessionId));
        }

        if (slotNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotNumber));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(requiredLogicalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(allocatedPhysicalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(allocatableBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingFreeBytes);


        SessionId = sessionId;
        SlotNumber = slotNumber;
        RequiredLogicalBytes = requiredLogicalBytes;
        AllocatedPhysicalBytes = allocatedPhysicalBytes;
        AllocatableBytes = allocatableBytes;
        RemainingFreeBytes = remainingFreeBytes;
        UsesInPlaceDataOverwrite = usesInPlaceDataOverwrite;
        Partitions = Clone(partitions);
        Extents = Clone(extents);
        Groups = Clone(groups);
        BlockDevices = Clone(blockDevices);
        DataWrites = Clone(dataWrites);
        GroupUsages = Clone(groupUsages);
        DataRollbackState = dataRollbackState;
    }

    internal Guid SessionId { get; }
    public int SlotNumber { get; }
    public long RequiredLogicalBytes { get; }
    public long AllocatedPhysicalBytes { get; }
    public long AllocatableBytes { get; }
    public long RemainingFreeBytes { get; }
    public bool UsesInPlaceDataOverwrite { get; }
    public ReadOnlyMemory<LpPartition> Partitions { get; }
    public ReadOnlyMemory<LpExtent> Extents { get; }
    public ReadOnlyMemory<LpPartitionGroup> Groups { get; }
    public ReadOnlyMemory<LpBlockDevice> BlockDevices { get; }
    public ReadOnlyMemory<LpPlannedDataWrite> DataWrites { get; }
    public ReadOnlyMemory<LpGroupUsage> GroupUsages { get; }

    private static ReadOnlyMemory<T> Clone<T>(T[]? source) =>
        source is null || source.Length == 0
            ? ReadOnlyMemory<T>.Empty
            : source.ToArray();

    public DataRollbackState DataRollbackState { get; }
}

public abstract record LpPlanResult
{
    private protected LpPlanResult()
    {
    }
}

public sealed record LpPlanSuccess : LpPlanResult
{
    public LpPlanSuccess(LpCommitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
    }

    public LpCommitPlan Plan { get; }
}

public sealed record LpPlanFailure : LpPlanResult
{
    public LpPlanFailure(LpEditFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    public LpEditFailure Failure { get; }
}

internal static class LpContractGuard
{
    internal static ReadOnlyMemory<object?> CloneArguments(IReadOnlyList<object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return ReadOnlyMemory<object?>.Empty;
        }

        var copy = new object?[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            copy[index] = arguments[index];
        }

        return copy;
    }

    internal static void Validate(LpEditErrorCode errorCode)
    {
        if (errorCode is < LpEditErrorCode.InvalidName or > LpEditErrorCode.InvalidSparseImage)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }
    }

    internal static void Validate(LpCommitErrorCode errorCode)
    {
        if (errorCode is < LpCommitErrorCode.InvalidVerificationOptions or > LpCommitErrorCode.CleanupCancelled)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }
    }

    internal static void ValidateIssue(
        VerificationStatus status,
        LpCommitIssue? issue,
        string parameterName)
    {
        var requiresIssue = status is VerificationStatus.Failed or VerificationStatus.Cancelled;
        if (requiresIssue != (issue is not null))
        {
            throw new ArgumentException(null, parameterName);
        }
    }

    internal static void ValidateIssue(
        CleanupStatus status,
        LpCommitIssue? issue,
        string parameterName)
    {
        var requiresIssue = status is CleanupStatus.Failed or CleanupStatus.Cancelled;
        if (requiresIssue != (issue is not null))
        {
            throw new ArgumentException(null, parameterName);
        }
    }
}
