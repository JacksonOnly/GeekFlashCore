using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeekFlashCore.Android.Lp;

/// <summary>
/// Owns an exclusive, fixed-length writable view of a single-file super image.
/// </summary>
public sealed class LpFileEditor : IDisposable
{
    private readonly WritableFileBlockDevice _device;
    private readonly LpEditSession _session;
    private readonly LpEditOpenOptions _openOptions;
    private readonly SingleFileResolver _resolver;
    private readonly ILogger<LpFileEditor> _logger;
    private int _operationState;

    private LpFileEditor(
        WritableFileBlockDevice device,
        LpEditSession session,
        LpEditOpenOptions openOptions,
        ILogger<LpFileEditor> logger)
    {
        _device = device;
        _session = session;
        _openOptions = openOptions;
        _resolver = new SingleFileResolver(device);
        _logger = logger;
    }

    public int SlotNumber => GetSession().SlotNumber;
    public LpGeometry Geometry => GetSession().Geometry;
    public LpDraft Draft => GetSession().Draft;
    public long FileLength => GetDevice().Length;

    public static LpFileEditor Open(
        string path,
        int slotNumber,
        LpEditOpenOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new LpEditOpenOptions();
        options.Validate();
        loggerFactory ??= NullLoggerFactory.Instance;

        WritableFileBlockDevice? device = null;
        LpEditSession? session = null;
        try
        {
            device = new WritableFileBlockDevice(path);
            session = new LpEditor(loggerFactory).Open(
                device,
                DeviceOwnership.Borrow,
                slotNumber,
                options);
            LpBlockDevice[] blockDevices = session.Snapshot.Baseline.BlockDevices;
            if (blockDevices.Length != 1)
            {
                throw new InvalidDataException(
                    Resources.SingleFileRequiresOneBlockDevice);
            }
            if ((ulong)device.Length < blockDevices[0].Size)
            {
                throw new InvalidDataException(
                    Resources.DeviceIdentityMismatch);
            }

            var result = new LpFileEditor(
                device,
                session,
                options,
                loggerFactory.CreateLogger<LpFileEditor>());
            LpLog.FileOpened(result._logger, slotNumber);
            device = null;
            session = null;
            return result;
        }
        catch
        {
            session?.Dispose();
            device?.Dispose();
            throw;
        }
    }

    public LpPlanResult CreatePlan(
        LpPlanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            return _session.CreatePlan(options, cancellationToken);
        }
        finally
        {
            ExitOperation();
        }
    }

    public async ValueTask<LpCommitResult> CommitAsync(
        LpCommitPlan plan,
        LpCommitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnterOperation();
        try
        {
            return await _session
                .CommitAsync(plan, _resolver, options, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>Exports a partition from the currently committed target-slot metadata.</summary>
    public async ValueTask ExportPartitionRawAsync(
        string partitionName,
        Stream destination,
        IProgress<BlockCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
        ArgumentNullException.ThrowIfNull(destination);
        EnterOperation();
        try
        {
            using LpMetadataSet metadata = LpMetadataSet.Open(
                _device,
                DeviceOwnership.Borrow,
                _openOptions.ReadLimits);
            using LpMetadataDocument document = metadata.OpenPreferredSlot(_session.SlotNumber);
            await LpRawPartitionExporter.ExportAsync(
                    document,
                    partitionName,
                    destination,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            LpLog.RawExportCompleted(_logger, partitionName);
        }
        finally
        {
            ExitOperation();
        }
    }

    public void Dispose()
    {
        int state = Interlocked.CompareExchange(ref _operationState, 2, 0);
        if (state == 2)
        {
            return;
        }
        if (state != 0)
        {
            throw new InvalidOperationException(Resources.ConcurrentSessionOperation);
        }

        Exception? failure = null;
        LpDisposal.TryDispose(_session, ref failure);
        LpDisposal.TryDispose(_device, ref failure);
        GC.SuppressFinalize(this);
        LpDisposal.ThrowIfFailed(failure);
    }

    private LpEditSession GetSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _operationState) == 2, this);
        return _session;
    }

    private WritableFileBlockDevice GetDevice()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _operationState) == 2, this);
        return _device;
    }

    private void EnterOperation()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _operationState) == 2, this);
        if (Interlocked.CompareExchange(ref _operationState, 1, 0) != 0)
        {
            throw new InvalidOperationException(Resources.ConcurrentSessionOperation);
        }
    }

    private void ExitOperation() => Interlocked.CompareExchange(ref _operationState, 0, 1);

    private sealed class SingleFileResolver(WritableFileBlockDevice device)
        : ILpWritableBlockDeviceResolver
    {
        public ValueTask<IWritableBlockDeviceLease> ResolveAsync(
            LpBlockDevice blockDevice,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((ulong)device.Length < blockDevice.Size)
            {
                throw new InvalidDataException(Resources.DeviceIdentityMismatch);
            }

            return ValueTask.FromResult<IWritableBlockDeviceLease>(
                new WritableBlockDeviceLease(device, DeviceOwnership.Borrow));
        }
    }
}
