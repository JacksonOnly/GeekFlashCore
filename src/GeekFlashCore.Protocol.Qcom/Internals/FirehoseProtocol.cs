using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal class FirehoseProtocol : IDisposable
{
    private bool _disposed;
    public bool IsConnected { get; private set; }
    private readonly ILogger _logger = Log.ForContext<FirehoseProtocol>();
    private readonly FirehoseCmdReceiver _receiver;
    private readonly FirehoseCmdSender _sender;
    private FirehoseTargetInfo _targetInfo;
    private readonly IProtocolConfig _config;

    public FirehoseProtocol(ITransport transport,IProtocolConfig config)
    {
        _config = config;
        _receiver = new FirehoseCmdReceiver(_logger, transport, config.ReadTimeoutMs);
        _sender = new FirehoseCmdSender(_logger, transport);
    }
    private void LogThroughput(string operation, long bytes, TimeSpan elapsed)
    {
        double mbps = elapsed.TotalSeconds > 0
            ? bytes / elapsed.TotalSeconds / (1024.0 * 1024.0)
            : 0;
        _logger.Information("{Operation} complete: {Bytes:N0} bytes in {Elapsed} ({Throughput:F2} MB/s)",
            operation, bytes, elapsed, mbps);
    }
    private void ThrowIfNotConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException(Strings.Firehose_NotConnected);
    }
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SaharaProtocol));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsConnected = false;
        _logger.Verbose("Disposing SaharaProtocol");
        _targetInfo = new FirehoseTargetInfo();
        GC.SuppressFinalize(this);
    }
}