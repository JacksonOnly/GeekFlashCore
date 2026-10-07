using System.Text;
using System.Xml;
using System.Diagnostics;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Internals;
using GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

internal sealed class FirehoseCommandExecutor
{
    private readonly FirehoseCmdReceiver _receiver;
    private readonly FirehoseCmdSender _sender;
    private OplusDigestConfiguration? _legacy;
    private Action? _legacyPacketSent;
    internal bool UsesLegacyBootstrap => _legacy is not null;

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

    internal bool StartupDataReceived => _receiver.StartupDataReceived;
    internal bool StartupAwaitingSignedTable => _receiver.StartupAwaitingSignedTable;

    public FirehoseResponse ReceiveStartupLogs(int? timeoutMilliseconds = null, int? probeRejectionTimeoutMilliseconds = null) =>
        _receiver.ReceiveStartupLog(timeoutMilliseconds, probeRejectionTimeoutMilliseconds);

    internal FirehoseProbeResult Probe(string? xmlDeclarationAttribute)
    {
        string xml = new NopCommand().Build();
        ValidateXml(xml);
        SendXml(ApplyXmlDeclarationAttribute(xml, xmlDeclarationAttribute), null);
        FirehoseProbeResult result = _receiver.ReceiveProbe();
        if (!result.AwaitingSignedTable) ValidateResponse(result.Response, expectedRawMode: false);
        return result;
    }

    public FirehoseCommandResult Execute(
        BaseCommand command,
        bool expectedRawMode,
        string? xmlDeclarationAttribute = null,
        Action? commandSent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        string xml = UsesLegacyBootstrap && command is ConfigureCommand configure
            ? OplusConfigureCommand.BuildCaptured(configure) : command.Build();
        ValidateXml(xml);
        SendXml(ApplyXmlDeclarationAttribute(xml, xmlDeclarationAttribute, UsesLegacyBootstrap && command is ConfigureCommand), commandSent);
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

    private static string ApplyXmlDeclarationAttribute(string xml, string? attribute, bool captured = false)
    {
        if (string.IsNullOrWhiteSpace(attribute))
            return xml;
        const string declarationEnd = "?>";
        int end = xml.IndexOf(declarationEnd, StringComparison.Ordinal);
        if (end < 0)
            return $"<?xml version=\"1.0\" encoding=\"UTF-8\" {attribute}{(captured ? " " : "")}?>" + xml;
        if (!captured)
            return xml[..end] + " " + attribute + declarationEnd + xml[(end + declarationEnd.Length)..];
        return xml[..end].TrimEnd() + " " + attribute + " " + declarationEnd + xml[(end + declarationEnd.Length)..];
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

    internal FirehoseCommandResult SendOplusSign(ReadOnlySpan<byte> signature, CancellationToken cancellationToken)
    {
        // A complete XML NAK permits the host to request a manual replacement. Do not
        // interpret a timeout, partial XML or raw-mode response as a recoverable rejection.
        _sender.SendRaw(signature.Length, signature, cancellationToken);
        _legacyPacketSent?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        FirehoseCommandResult result = ToResult(_receiver.Receive(cancellationToken: cancellationToken));
        if (result.RawMode) throw new FirehoseProtocolException(Strings.Qcom_FirehoseRawModeUnexpected);
        return result;
    }

    internal FirehoseCommandResult BeginOplusVerify(string? declarationAttribute, CancellationToken cancellationToken)
    {
        string xml = UsesLegacyBootstrap
            ? "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><verify EnableVip=\"0\"/></data>"
            : "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><verify value=\"ping\" EnableVip=\"1\"/></data>";
        cancellationToken.ThrowIfCancellationRequested();
        SendXml(ApplyXmlDeclarationAttribute(xml, declarationAttribute, UsesLegacyBootstrap), null);
        FirehoseCommandResult result = ToResult(_receiver.Receive(cancellationToken: cancellationToken, rejectOplusRestart: true));
        ThrowIfNak(result);
        // Reference loaders can omit rawmode or set it true; either ACK is a
        // request for the following 4096-byte Sign, never permission to send XML.
        return result;
    }

    internal bool InitializeOplusSha256(string? declarationAttribute, CancellationToken cancellationToken)
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><sha256init Verbose=\"1\"/></data>";
        if (!UsesLegacyBootstrap)
        {
            ExecuteXml(xml, expectedRawMode: false, declarationAttribute, cancellationToken: cancellationToken);
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        SendXml(ApplyXmlDeclarationAttribute(xml, declarationAttribute, captured: true), null);
        FirehoseResponse? response = _receiver.ReceiveOplusInitialization(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (response is null) throw new TimeoutException(Strings.Qcom_OplusSha256InitUnconfirmed);
        if (response.RawMode) throw new FirehoseProtocolException(Strings.Qcom_FirehoseRawModeUnexpected);
        FirehoseCommandResult result = ToResult(response);
        // Optional receives use empty attributes for completed log-only replies.
        // This single captured error is compatibility evidence, never a synthetic ACK.
        ThrowIfNak(result);
        if (response.Logs.Any(static log =>
                log.Message.Contains("VIP is enabled", StringComparison.OrdinalIgnoreCase) ||
                (log.Level == FirehoseLogLevel.Error && log.Message.Trim() != "Failed to run the last command -1")))
            throw new FirehoseProtocolException(Strings.Qcom_OplusSha256InitUnconfirmed);
        if (response.Attributes.ContainsKey("value"))
        {
            return false;
        }
        if (response.Logs.Any(static log =>
                log.Level == FirehoseLogLevel.Error && log.Message.Trim() == "Failed to run the last command -1"))
            return true;
        throw new FirehoseProtocolException(Strings.Qcom_OplusSha256InitUnconfirmed);
    }

    internal FirehoseCommandResult ReadOplusRejectionDetails(FirehoseCommandResult result, int timeout,
        CancellationToken cancellationToken)
    {
        FirehoseResponse? tail = _receiver.ReceiveOptional(timeout, cancellationToken);
        if (tail is null) return result;
        if (tail.RawMode) throw new FirehoseProtocolException(Strings.Qcom_FirehoseRawModeUnexpected);
        return result with { Logs = result.Logs.Concat(tail.Logs).ToArray() };
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

        // Preserve device text in Result for internal compatibility decisions;
        // exception messages are also printed by hosts and must never echo it.
        throw new FirehoseNakException(Strings.Qcom_FirehoseCommandRejected, result);
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
