using System.Buffers;
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

    public FirehoseCmdReceiver(ILogger logger, ITransport transport, int readTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (readTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(readTimeoutMilliseconds));
        _reader = new FirehoseWireReader(transport);
        _readTimeoutMilliseconds = readTimeoutMilliseconds;
        _logger = logger;
    }

    public FirehoseResponse ReceiveStartupLog(int? timeoutMilliseconds = null)
    {
        FirehoseResponse response = _reader.ReadStartupLogs(
            timeoutMilliseconds ?? _readTimeoutMilliseconds);
        PublishLogs(response);
        return response;
    }

    public FirehoseResponse Receive(bool publishLogs = true, CancellationToken cancellationToken = default)
    {
        FirehoseResponse response = _reader.ReadResponse(_readTimeoutMilliseconds, cancellationToken);
        if (publishLogs) PublishLogs(response);
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

    internal FirehoseResponse? ReceiveOptional(int timeout, CancellationToken cancellationToken) =>
        _reader.ReadOptionalResponse(timeout, cancellationToken);

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

    private void PublishLogs(FirehoseResponse response)
    {
        foreach (FirehoseResponseLog log in response.Logs)
        {
            string message = QcomDeviceText.ForDisplay(log.Message);
            if (log.Level == FirehoseLogLevel.Info &&
                log.Message.StartsWith("Calling handler for ", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug(Strings.Qcom_LogDeviceMessage, message);
                continue;
            }

            switch (log.Level)
            {
                case FirehoseLogLevel.Error:
                _logger.Error(Strings.Qcom_LogDeviceMessage, message);
                    break;
                case FirehoseLogLevel.Warn:
                _logger.Warning(Strings.Qcom_LogDeviceMessage, message);
                    break;
                case FirehoseLogLevel.Debug:
                _logger.Debug(Strings.Qcom_LogDeviceMessage, message);
                    break;
                default:
                _logger.Information(Strings.Qcom_LogDeviceMessage, message);
                    break;
            }
        }
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
