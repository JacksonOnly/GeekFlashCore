using System.Security.Cryptography;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice.Abstractions;
using Microsoft.Extensions.Logging;

namespace GeekFlashCore.Android.Lp;

public sealed class LpEditSession : IDisposable
{
    private readonly LpMetadataSet _metadata;
    private readonly LpEditOpenOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<LpCommitPlan, LpPlanState> _plans =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<LpPartitionImageSource> _images =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _lifetimeSync = new();
    private int _operationState;
    private int _disposed;
    private int _committed;

    internal LpEditSession(
        LpMetadataSet metadata,
        LpSessionSnapshot snapshot,
        int slotNumber,
        LpEditOpenOptions options,
        ILoggerFactory loggerFactory)
    {
        _metadata = metadata;
        Snapshot = snapshot;
        SlotNumber = slotNumber;
        _options = options;
        _loggerFactory = loggerFactory;
        SessionId = Guid.NewGuid();
        Draft = new LpDraft(this, snapshot.Baseline);
    }

    public int SlotNumber { get; }
    public LpGeometry Geometry => Snapshot.Geometry;
    public LpDraft Draft { get; }

    internal Guid SessionId { get; }
    internal LpSessionSnapshot Snapshot { get; }
    internal ushort MetadataMinorVersion => Snapshot.Baseline.Header.MinorVersion;
    internal IReadableBlockDevice MetadataSource => _metadata.Source;
    internal GeekFlashCore.ImageFormats.Abstractions.ImageReadLimits ReadLimits => _options.ReadLimits;
    internal LpWriteLimits WriteLimits => _options.WriteLimits;

    public LpPlanResult CreatePlan(
        LpPlanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LpPlanOptions();
        EnterOperation();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSourceUnchanged(cancellationToken))
            {
                return new LpPlanFailure(new LpEditFailure(
                    LpEditErrorCode.SourceChanged,
                    "Lp.Edit.SourceChanged",
                    diagnosticContext: new LpDiagnosticContext(slotNumber: SlotNumber)));
            }

            LpDraftSnapshot draft = Draft.CaptureSnapshot();
            LpPlanResult result = LpLayoutPlanner.CreatePlan(
                this,
                draft,
                options,
                cancellationToken,
                out LpPlanState? state);
            if (result is LpPlanSuccess success && state is not null)
            {
                _plans.Add(success.Plan, state);
            }
            return result;
        }
        finally
        {
            ExitOperation();
        }
    }

    public async ValueTask<LpCommitResult> CommitAsync(
        LpCommitPlan plan,
        ILpWritableBlockDeviceResolver resolver,
        LpCommitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(resolver);
        if (plan.SessionId != SessionId)
        {
            throw new ArgumentException(
                Resources.PlanDoesNotBelongToSession,
                nameof(plan));
        }
        options ??= new LpCommitOptions();

        EnterOperation();
        try
        {
            if (!_plans.TryGetValue(plan, out LpPlanState? state))
            {
                throw new ArgumentException(
                    Resources.PlanDoesNotBelongToSession,
                    nameof(plan));
            }

            LpCommitResult result = await LpCommitter.CommitAsync(
                    this,
                    state,
                    resolver,
                    options,
                    _loggerFactory.CreateLogger<LpCommitter>(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.CommitSucceeded)
            {
                Volatile.Write(ref _committed, 1);
            }
            return result;
        }
        finally
        {
            ExitOperation();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _operationState, 2, 0) != 0)
        {
            Volatile.Write(ref _disposed, 0);
            throw new InvalidOperationException(Resources.ConcurrentSessionOperation);
        }

        Exception? failure = null;
        lock (_lifetimeSync)
        {
            foreach (LpPartitionImageSource image in _images)
            {
                LpDisposal.TryDispose(image, ref failure);
            }
            _images.Clear();
            _plans.Clear();
            LpDisposal.TryDispose(_metadata, ref failure);
        }
        GC.SuppressFinalize(this);
        LpDisposal.ThrowIfFailed(failure);
    }

    internal void RegisterImage(LpPartitionImageSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_lifetimeSync)
        {
            _images.Add(image);
        }
    }

    internal MutationLease EnterMutation()
    {
        EnterOperation();
        return new MutationLease(this);
    }

    internal bool IsSourceUnchanged(CancellationToken cancellationToken = default)
    {
        IReadableBlockDevice source = _metadata.Source;
        if (source.Id != Snapshot.MetadataDevice.Id ||
            source.Length != Snapshot.MetadataDevice.Length ||
            source.LogicalBlockSize != Snapshot.MetadataDevice.LogicalBlockSize)
        {
            return false;
        }

        byte[] primaryGeometry = LpSnapshotReader.HashRange(
            source,
            LpFormat.ReservedBytes,
            LpFormat.GeometryBlockSize,
            cancellationToken);
        byte[] backupGeometry = LpSnapshotReader.HashRange(
            source,
            LpFormat.ReservedBytes + LpFormat.GeometryBlockSize,
            LpFormat.GeometryBlockSize,
            cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                primaryGeometry,
                Snapshot.PrimaryGeometryDigest) ||
            !CryptographicOperations.FixedTimeEquals(
                backupGeometry,
                Snapshot.BackupGeometryDigest))
        {
            return false;
        }

        foreach (LpCopySnapshot[] slot in Snapshot.Slots)
        {
            foreach (LpCopySnapshot copy in slot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] current = LpSnapshotReader.HashMetadataCopy(
                    source,
                    Snapshot.Geometry,
                    copy.SlotNumber,
                    copy.Kind,
                    cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(current, copy.Digest))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private void EnterOperation()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _committed) != 0 ||
            Interlocked.CompareExchange(ref _operationState, 1, 0) != 0)
        {
            throw new InvalidOperationException(Resources.ConcurrentSessionOperation);
        }
    }

    private void ExitOperation() => Interlocked.CompareExchange(ref _operationState, 0, 1);

    internal readonly struct MutationLease : IDisposable
    {
        private readonly LpEditSession? _session;

        internal MutationLease(LpEditSession session) => _session = session;

        public void Dispose() => _session?.ExitOperation();
    }
}
