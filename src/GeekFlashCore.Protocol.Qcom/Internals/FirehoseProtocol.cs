using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;
using GeekFlashCore.Protocol.Qcom.Firehose.Configuration;
using GeekFlashCore.Protocol.Qcom.Firehose.Storage;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal sealed class FirehoseProtocol : IDisposable
{
    private readonly ILogger _logger = Log.ForContext<FirehoseProtocol>();
    private readonly FirehoseSession _session;
    private FirehoseStorageService? _storageService;
    private FirehoseTargetInfo _targetInfo;
    private bool _disposed;

    public FirehoseProtocol(ITransport transport, IProtocolConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _session = new FirehoseSession(transport, config.ReadTimeoutMs);
        _targetInfo = new FirehoseTargetInfo();
    }

    public bool IsConnected => _session.State is FirehoseSessionState.Started
        or FirehoseSessionState.Configured
        or FirehoseSessionState.RawTransfer;

    public FirehoseTargetInfo TargetInfo => _targetInfo;

    public FirehoseResponse Start(int? startupTimeoutMilliseconds = null)
    {
        ThrowIfDisposed();
        return _session.Start(startupTimeoutMilliseconds);
    }

    public void StartWithoutStartupLogs()
    {
        ThrowIfDisposed();
        _session.StartWithoutStartupLogs();
    }

    public FirehoseCommandResult Execute(BaseCommand command, bool expectedRawMode = false)
    {
        ThrowIfDisposed();
        ThrowIfNotConnected();
        return _session.Execute(command, expectedRawMode);
    }

    public FirehoseCommandResult ExecuteXml(string xml, bool expectedRawMode = false)
    {
        ThrowIfDisposed();
        ThrowIfNotConnected();
        return _session.ExecuteXml(xml, expectedRawMode);
    }

    public FirehoseConfigureResult Configure(
        FirehoseConfiguration configuration,
        QcomVendorKind vendor = QcomVendorKind.Generic,
        Func<FirehoseNakException, bool>? xiaomiAuthentication = null)
    {
        ThrowIfDisposed();
        ThrowIfNotConnected();
        FirehoseConfigureResult result = new ConfigureNegotiator(_session).Negotiate(
            configuration,
            vendor,
            xiaomiAuthentication);
        _targetInfo.Configuration = result.Configuration;
        _targetInfo.TargetName = result.Configuration.TargetName;
        _storageService = new FirehoseStorageService(_session, result.Configuration);
        return result;
    }

    public FirehoseStorageInfo GetStorageInfo(uint physicalPartitionNumber)
    {
        ThrowIfDisposed();
        FirehoseStorageInfo info = GetStorageService().GetStorageInfo(physicalPartitionNumber);
        _targetInfo.StorageInfos =
        [
            .. _targetInfo.StorageInfos.Where(item =>
                item.PhysicalPartitionNumber != physicalPartitionNumber),
            info
        ];
        return info;
    }

    public FirehoseBasicDevInfo GetBasicDeviceInfo()
    {
        ThrowIfDisposed();
        FirehoseBasicDevInfo info = GetStorageService().GetBasicDeviceInfo();
        _targetInfo.BasicDevCharacteristics = info;
        return info;
    }

    public FirehoseStorageService GetStorageService()
    {
        ThrowIfDisposed();
        return _storageService ?? throw new InvalidOperationException(Strings.Qcom_FirehoseNotConfigured);
    }

    private void LogThroughput(string operation, long bytes, TimeSpan elapsed)
    {
        double mbps = elapsed.TotalSeconds > 0
            ? bytes / elapsed.TotalSeconds / (1024.0 * 1024.0)
            : 0;
        _logger.Information(
            Strings.Qcom_LogOperationComplete,
            operation,
            bytes,
            elapsed,
            mbps);
    }

    private void ThrowIfNotConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException(Strings.Firehose_NotConnected);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FirehoseProtocol));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _logger.Verbose(Strings.Qcom_LogDisposeFirehose);
        _session.Dispose();
        _storageService = null;
        _targetInfo = new FirehoseTargetInfo();
        GC.SuppressFinalize(this);
    }
}
