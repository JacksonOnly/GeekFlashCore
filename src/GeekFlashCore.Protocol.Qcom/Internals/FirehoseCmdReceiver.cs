using System.Buffers;
using System.Diagnostics;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Transport.Abstractions;
using Serilog;
using Serilog.Events;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal sealed class FirehoseCmdReceiver : IDisposable
{
    private readonly FirehoseWireReader _reader;
    private int _readTimeoutMilliseconds;
    private readonly ILogger _logger;
    private readonly ILogger _deviceLogger;
    private readonly Action<FirehoseResponseLog> _publishLog;
    private readonly Action<FirehoseResponseLog> _publishOplusVerifyLog;

    public FirehoseCmdReceiver(ILogger logger, ITransport transport, int readTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (readTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(readTimeoutMilliseconds));
        _reader = new FirehoseWireReader(transport);
        _readTimeoutMilliseconds = readTimeoutMilliseconds;
        _logger = logger;
        _deviceLogger = logger.ForContext("DeviceDiagnostic", true);
        _publishLog = PublishLog;
        _publishOplusVerifyLog = PublishOplusVerifyLog;
    }

    internal bool StartupDataReceived => _reader.StartupDataReceived;

    public FirehoseResponse ReceiveStartupLog(int? timeoutMilliseconds = null)
    {
        long started = Stopwatch.GetTimestamp();
        _logger.Debug(Strings.Qcom_LogStartupWait, timeoutMilliseconds ?? _readTimeoutMilliseconds);
        FirehoseResponse response;
        try { response = _reader.ReadStartupLogs(timeoutMilliseconds ?? _readTimeoutMilliseconds, PublishLog); }
        catch (Exception exception)
        {
            _logger.Debug(Strings.Qcom_LogResponseInterrupted, exception.GetType().Name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, timeoutMilliseconds ?? _readTimeoutMilliseconds);
            throw;
        }
        _logger.Debug(Strings.Qcom_LogResponseTiming, response.Status, response.RawMode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, timeoutMilliseconds ?? _readTimeoutMilliseconds);
        return response;
    }

    public FirehoseResponse Receive(bool publishLogs = true, CancellationToken cancellationToken = default,
        bool rejectOplusRestart = false)
    {
        long started = Stopwatch.GetTimestamp();
        FirehoseResponse response;
        try { response = _reader.ReadResponse(_readTimeoutMilliseconds, cancellationToken,
            rejectOplusRestart ? _publishOplusVerifyLog : publishLogs ? _publishLog : null); }
        catch (Exception exception)
        {
            _logger.Debug(Strings.Qcom_LogResponseInterrupted, exception.GetType().Name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, _readTimeoutMilliseconds);
            throw;
        }
        _logger.Debug(Strings.Qcom_LogResponseTiming, response.Status, response.RawMode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, _readTimeoutMilliseconds);
        if (HasDiagnosticAttributes(response) && _logger.IsEnabled(LogEventLevel.Debug))
        {
            _logger.Debug(Strings.Qcom_LogFirehoseResponseAttributes,
                response.Status,
                response.Attributes.Count,
                string.Join(", ", response.Attributes.Keys.Order(StringComparer.OrdinalIgnoreCase)));
        }
        return response;
    }

    public FirehoseResponse? PollResponse() => _reader.PollResponse();

    internal FirehoseResponse? ReceiveOptional(int timeout, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        FirehoseResponse? response = _reader.ReadOptionalResponse(timeout, cancellationToken, _publishLog);
        _logger.Debug(Strings.Qcom_LogResponseTiming,
            response?.Attributes.ContainsKey("value") == true ? response.Status : null, response?.RawMode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, timeout);
        return response;
    }

    internal FirehoseResponse? ReceiveOplusInitialization(CancellationToken cancellationToken) =>
        ReceiveOptional(Math.Min(1500, _readTimeoutMilliseconds), cancellationToken);

    internal FirehoseResponse ReceiveWithin(int timeout, CancellationToken cancellationToken) =>
        _reader.ReadResponse(timeout, cancellationToken);

    internal void DiscardBuffered() => _reader.DiscardBuffered();

    internal void SetReadTimeout(int milliseconds)
    {
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        _readTimeoutMilliseconds = milliseconds;
    }

    public FirehoseResponse ReceiveRaw(
        Span<byte> destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        long completed = 0;
        while (completed < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = _reader.ReadRaw(destination[(int)completed..], _readTimeoutMilliseconds);
            if (read <= 0)
                throw new EndOfStreamException(Strings.Firehose_RawPayloadEndedEarly);
            completed += read;
            progress?.Report(completed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Receive(cancellationToken: cancellationToken);
    }

    public FirehoseResponse ReceiveRaw(
        int bufferSize,
        long lengthToRead,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException(Strings.Firehose_DestinationStreamNotWritable, nameof(destination));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if (lengthToRead < 0)
            throw new ArgumentOutOfRangeException(nameof(lengthToRead));

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(bufferSize, Math.Max(1, lengthToRead)));
        try
        {
            long completed = 0;
            while (completed < lengthToRead)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(bufferSize, lengthToRead - completed);
                int read = _reader.ReadRaw(buffer.AsSpan(0, count), _readTimeoutMilliseconds);
                if (read <= 0)
                    throw new EndOfStreamException(Strings.Firehose_RawPayloadEndedEarly);
                destination.Write(buffer.AsSpan(0, read));
                completed += read;
                progress?.Report(completed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Receive(cancellationToken: cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void PublishOplusVerifyLog(FirehoseResponseLog log)
    {
        PublishLog(log);
        if (log.Message.Contains("VIP is enabled, receiving the signed table", StringComparison.OrdinalIgnoreCase))
            throw new QcomAuthenticationException(Strings.Qcom_OplusVerifyRestarted);
    }

    private void PublishLog(FirehoseResponseLog log)
    {
        string message = QcomDeviceText.ForDisplay(log.Message);
        LogEventLevel level = log.Level switch
        {
            FirehoseLogLevel.Error => LogEventLevel.Error,
            FirehoseLogLevel.Warn => LogEventLevel.Warning,
            _ => LogEventLevel.Debug
        };
        _deviceLogger.ForContext("DeviceLogLevel", log.Level)
            .ForContext("DeviceTextLength", log.Message.Length)
            .Write(level, Strings.Qcom_LogDeviceMessage, message);
    }

    private static bool HasDiagnosticAttributes(FirehoseResponse response)
    {
        if (response.Attributes.Count == 0)
            return false;
        if (response.Status != FirehoseResponseStatus.Ack)
            return true;

        foreach (string key in response.Attributes.Keys)
        {
            if (!key.Equals("value", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("rawmode", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Dispose() => _reader.Dispose();
}
