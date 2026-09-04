using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal sealed class FirehoseProtocol : IDisposable
{
    private readonly ILogger _logger = Log.ForContext<FirehoseProtocol>();
    private readonly FirehoseSession _session;
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

    private void LogThroughput(string operation, long bytes, TimeSpan elapsed)
    {
        double mbps = elapsed.TotalSeconds > 0
            ? bytes / elapsed.TotalSeconds / (1024.0 * 1024.0)
            : 0;
        _logger.Information(
            "{Operation} complete: {Bytes:N0} bytes in {Elapsed} ({Throughput:F2} MB/s)",
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
        _logger.Verbose("Disposing FirehoseProtocol");
        _session.Dispose();
        _targetInfo = new FirehoseTargetInfo();
        GC.SuppressFinalize(this);
    }
}
