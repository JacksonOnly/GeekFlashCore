using System.Text;
using System.Xml;
using System.Diagnostics;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Internals;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

internal sealed class FirehoseCommandExecutor
{
    private readonly FirehoseCmdReceiver _receiver;
    private readonly FirehoseCmdSender _sender;
    private OplusDigestConfiguration? _legacy;
    private Action? _legacyPacketSent;

    internal void ConfigureLegacy(OplusDigestConfiguration configuration, Action packetSent)
    {
        FirehoseLegacyXml.ValidateNop(configuration.NopXml ?? FirehoseLegacyXml.DefaultNop);
        _legacy = configuration;
        _legacyPacketSent = packetSent;
    }

    private void SendXml(string xml, Action? commandSent, bool flush = true)
    {
        if (_legacy is not null && flush)
        {
            _sender.Flush();
            _receiver.DiscardBuffered();
        }
        _sender.SendXml(xml, () => { commandSent?.Invoke(); _legacyPacketSent?.Invoke(); }, _legacy?.MaximumXmlSendSize);
    }

    public FirehoseCommandExecutor(FirehoseCmdSender sender, FirehoseCmdReceiver receiver)
    {
        _sender = sender;
        _receiver = receiver;
    }

    public FirehoseResponse ReceiveStartupLogs(int? timeoutMilliseconds = null) =>
        _receiver.ReceiveStartupLog(timeoutMilliseconds);

    public FirehoseCommandResult Execute(
        BaseCommand command,
        bool expectedRawMode,
        string? xmlDeclarationAttribute = null,
        Action? commandSent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        string xml = command.Build();
        ValidateXml(xml);
        SendXml(ApplyXmlDeclarationAttribute(xml, xmlDeclarationAttribute), commandSent);
        bool publishDeviceText = command is not (PeekCommand or PokeCommand or GetSha256DigestCommand);
        return ValidateResponse(_receiver.Receive(publishDeviceText, cancellationToken), expectedRawMode, publishDeviceText);
    }

    public FirehoseCommandResult ExecuteXml(
        string xml,
        bool expectedRawMode,
        string? xmlDeclarationAttribute = null,
        Action? commandSent = null,
        CancellationToken cancellationToken = default)
    {
        ValidateXml(_legacy is null ? xml : FirehoseLegacyXml.ForValidation(xml));
        SendXml(ApplyXmlDeclarationAttribute(xml, xmlDeclarationAttribute), commandSent);
        return ValidateResponse(_receiver.Receive(cancellationToken: cancellationToken), expectedRawMode);
    }

    private static string ApplyXmlDeclarationAttribute(string xml, string? attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute))
            return xml;
        const string declarationEnd = "?>";
        int end = xml.IndexOf(declarationEnd, StringComparison.Ordinal);
        if (end < 0)
            return $"<?xml version=\"1.0\" encoding=\"UTF-8\" {attribute}?>" + xml;
        return xml[..end] + " " + attribute + declarationEnd + xml[(end + declarationEnd.Length)..];
    }

    public FirehoseCommandResult SendRaw(
        ReadOnlySpan<byte> source,
        int bufferSize,
        CancellationToken cancellationToken,
        Action? packetSent)
    {
        _sender.SendRaw(bufferSize, source, cancellationToken, () => CheckRawResponse(packetSent));
        _legacyPacketSent?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return CompleteRawTransfer(_receiver.Receive(cancellationToken: cancellationToken), source.Length);
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
        _legacyPacketSent?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return CompleteRawTransfer(_receiver.Receive(cancellationToken: cancellationToken), wireLength) with { Sha256Digest = digest };
    }

    internal FirehoseCommandResult? SendLegacyDigest(Stream source, long length, int bufferSize,
        int timeout, CancellationToken cancellationToken)
    {
        _sender.SendRaw(bufferSize, source, length, length, 0, cancellationToken: cancellationToken);
        _legacyPacketSent?.Invoke();
        FirehoseResponse? response = _receiver.ReceiveOptional(timeout, cancellationToken);
        if (response is null) return null;
        FirehoseCommandResult result = ToResult(response);
        ThrowIfNak(result);
        if (result.RawMode) throw new FirehoseProtocolException(Strings.Qcom_FirehoseRawModeUnexpected);
        return result;
    }

    internal FirehoseCommandResult ConfirmLegacyNop(string xml, bool requireHandler, int timeout,
        CancellationToken cancellationToken)
    {
        FirehoseLegacyXml.ValidateNop(xml);
        cancellationToken.ThrowIfCancellationRequested();
        SendXml(xml, null, flush: !requireHandler);
        long started = Stopwatch.GetTimestamp();
        bool handler = !requireHandler;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int remaining = timeout - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0) throw new TimeoutException(Strings.Qcom_LegacyNopUnconfirmed);
            FirehoseCommandResult result = ToResult(_receiver.ReceiveWithin(remaining, cancellationToken));
            ThrowIfNak(result);
            if (result.RawMode) throw new FirehoseProtocolException(Strings.Qcom_FirehoseRawModeUnexpected);
            handler |= result.Logs.Any(static log => log.Message.Contains("Calling handler for nop", StringComparison.Ordinal));
            if (handler) return result;
        }
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

    private FirehoseCommandResult ValidateResponse(FirehoseResponse response, bool expectedRawMode, bool publishDeviceText = true)
    {
        FirehoseCommandResult result = ToResult(response);
        if (!publishDeviceText && !result.IsSuccess)
            throw new FirehoseNakException(Strings.Qcom_FirehoseCommandRejected, result);
        ThrowIfNak(result);
        if (_legacy is not null && expectedRawMode && !result.Attributes.ContainsKey("rawmode"))
            result = result with { RawMode = true };
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

    internal static void ValidateXml(string xml)
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
