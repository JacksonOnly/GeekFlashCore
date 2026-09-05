using System.Text;
using System.Xml;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Internals;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

internal sealed class FirehoseCommandExecutor
{
    private readonly FirehoseCmdReceiver _receiver;
    private readonly FirehoseCmdSender _sender;

    public FirehoseCommandExecutor(FirehoseCmdSender sender, FirehoseCmdReceiver receiver)
    {
        _sender = sender;
        _receiver = receiver;
    }

    public FirehoseResponse ReceiveStartupLogs(int? timeoutMilliseconds = null) =>
        _receiver.ReceiveStartupLog(timeoutMilliseconds);

    public FirehoseCommandResult Execute(BaseCommand command, bool expectedRawMode)
    {
        ArgumentNullException.ThrowIfNull(command);
        _sender.SendCommand(command);
        return ValidateResponse(_receiver.Receive(), expectedRawMode);
    }

    public FirehoseCommandResult ExecuteXml(string xml, bool expectedRawMode)
    {
        ValidateXml(xml);
        _sender.SendXml(xml);
        return ValidateResponse(_receiver.Receive(), expectedRawMode);
    }

    public FirehoseCommandResult SendRaw(
        ReadOnlySpan<byte> source,
        int bufferSize,
        CancellationToken cancellationToken,
        Action? packetSent)
    {
        _sender.SendRaw(bufferSize, source, cancellationToken, () => CheckRawResponse(packetSent));
        return CompleteRawTransfer(_receiver.Receive(), source.Length);
    }

    public FirehoseCommandResult SendRaw(
        Stream source,
        long sourceLength,
        long wireLength,
        int bufferSize,
        byte paddingByte,
        IProgress<long>? progress,
        CancellationToken cancellationToken,
        bool computeDigest,
        Action? packetSent)
    {
        byte[] digest = _sender.SendRaw(
            bufferSize,
            source,
            sourceLength,
            wireLength,
            paddingByte,
            progress,
            cancellationToken,
            computeDigest,
            () => CheckRawResponse(packetSent));
        return CompleteRawTransfer(_receiver.Receive(), wireLength) with { Sha256Digest = digest };
    }

    private void CheckRawResponse(Action? packetSent)
    {
        packetSent?.Invoke();
        FirehoseResponse? available = _receiver.PollResponse();
        if (available is not null && available.Status != FirehoseResponseStatus.Ack)
            ThrowIfNak(ToResult(_receiver.Receive()));
    }

    public FirehoseCommandResult ReceiveRaw(
        Span<byte> destination,
        IProgress<long>? progress,
        CancellationToken cancellationToken) =>
        CompleteRawTransfer(_receiver.ReceiveRaw(destination, progress, cancellationToken), destination.Length);

    public FirehoseCommandResult ReceiveRaw(
        Stream destination,
        long length,
        int bufferSize,
        IProgress<long>? progress,
        CancellationToken cancellationToken) =>
        CompleteRawTransfer(
            _receiver.ReceiveRaw(bufferSize, length, destination, progress, cancellationToken),
            length);

    private static FirehoseCommandResult ValidateResponse(FirehoseResponse response, bool expectedRawMode)
    {
        FirehoseCommandResult result = ToResult(response);
        ThrowIfNak(result);
        if (result.RawMode != expectedRawMode)
        {
            throw new FirehoseProtocolException(
                Strings.FormatQcom_FirehoseRawmodeMismatch(result.RawMode, expectedRawMode));
        }
        return result;
    }

    private static FirehoseCommandResult CompleteRawTransfer(FirehoseResponse response, long bytesTransferred)
    {
        FirehoseCommandResult result = ToResult(response) with { BytesTransferred = bytesTransferred };
        ThrowIfNak(result);
        if (result.RawMode)
            throw new FirehoseProtocolException(Strings.Qcom_FirehoseRawModeUnexpected);
        return result;
    }

    private static FirehoseCommandResult ToResult(FirehoseResponse response) => new()
    {
        Status = response.Status,
        RawMode = response.RawMode,
        Attributes = response.Attributes,
        Logs = response.Logs,
        PayloadElements = response.PayloadElements
    };

    private static void ThrowIfNak(FirehoseCommandResult result)
    {
        if (result.Status == FirehoseResponseStatus.Ack)
            return;

        string message = result.Logs.LastOrDefault(static log => log.Level == FirehoseLogLevel.Error)?.Message
                         ?? result.Logs.LastOrDefault()?.Message
                         ?? (result.Attributes.TryGetValue("reason", out string? reason) ? reason : null)
                         ?? Strings.Qcom_FirehoseCommandRejected;
        throw new FirehoseNakException(message, result);
    }

    private static void ValidateXml(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        if (Encoding.UTF8.GetByteCount(xml) > FirehoseConstants.MaximumXmlPacketSize)
            throw new ArgumentException(Strings.Qcom_FirehoseXmlTooLargeConfigured, nameof(xml));

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                ConformanceLevel = ConformanceLevel.Document,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = FirehoseConstants.MaximumXmlPacketSize,
                MaxCharactersFromEntities = 0
            };
            using var stringReader = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(stringReader, settings);
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 0 ||
                !reader.Name.Equals("data", StringComparison.Ordinal) || reader.IsEmptyElement)
            {
            throw new XmlException(Strings.Qcom_FirehoseXmlRootInvalid);
            }

            int commandCount = 0;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
                    commandCount++;
            }
            if (commandCount != 1)
            throw new XmlException(Strings.Qcom_FirehoseXmlCommandCountInvalid);
        }
        catch (XmlException exception)
        {
            throw new ArgumentException(Strings.Qcom_FirehoseXmlInvalidUnsafe, nameof(xml), exception);
        }
    }
}
