using System.Text;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Internals;

namespace GeekFlashCore.Protocol.Qcom.Extensions;

internal static class FirehoseResponseParser
{
    private static ReadOnlySpan<byte> LogTag => "log"u8;
    private static ReadOnlySpan<byte> ResponseTag => "response"u8;
    private static ReadOnlySpan<byte> DataTag => "data"u8;

    public static bool TryParsePacket(
        ReadOnlySpan<byte> xml,
        List<FirehoseResponseLog> logs,
        out FirehoseResponseStatus status,
        out bool rawMode,
        out IReadOnlyDictionary<string, string>? attributes,
        out IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? payloadElements)
    {
        status = FirehoseResponseStatus.Nak;
        rawMode = false;
        attributes = null;
        payloadElements = null;

        Dictionary<string, string>? responseAttributes = null;
        Dictionary<string, IReadOnlyDictionary<string, string>>? elements = null;
        ReadOnlySpan<byte> remaining = xml;
        while (FirehoseXmlScanner.TryReadNextElement(
                   remaining,
                   out ReadOnlySpan<byte> name,
                   out Dictionary<string, string>? elementAttributes,
                   out remaining))
        {
            if (name.SequenceEqual(LogTag))
            {
                AddLog(elementAttributes, logs);
            }
            else if (name.SequenceEqual(ResponseTag))
            {
                responseAttributes = elementAttributes;
            }
            else if (!name.SequenceEqual(DataTag))
            {
                elements ??= new Dictionary<string, IReadOnlyDictionary<string, string>>(
                    StringComparer.OrdinalIgnoreCase);
                elements[Encoding.UTF8.GetString(name)] =
                    elementAttributes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        if (responseAttributes is null)
            return false;

        string value = FirehoseXmlScanner.GetAttributeString(responseAttributes, "value");
        status = value.Equals("ACK", StringComparison.OrdinalIgnoreCase)
            ? FirehoseResponseStatus.Ack
            : FirehoseResponseStatus.Nak;
        rawMode = FirehoseXmlScanner.GetAttributeString(responseAttributes, "rawmode")
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        attributes = responseAttributes;
        payloadElements = elements;
        return true;
    }

    public static void ParseLogs(ReadOnlySpan<byte> xml, List<FirehoseResponseLog> logs)
    {
        ReadOnlySpan<byte> remaining = xml;
        while (FirehoseXmlScanner.TryReadNextElement(
                   remaining,
                   out ReadOnlySpan<byte> name,
                   out Dictionary<string, string>? attributes,
                   out remaining))
        {
            if (name.SequenceEqual(LogTag))
                AddLog(attributes, logs);
        }
    }

    private static void AddLog(
        Dictionary<string, string>? attributes,
        List<FirehoseResponseLog> logs)
    {
        string value = FirehoseXmlScanner.GetAttributeString(attributes, "value");
        string level = FirehoseXmlScanner.GetAttributeString(attributes, "level");
        FirehoseXmlScanner.SplitLogValue(value, out string embeddedLevel, out string message);
        if (string.IsNullOrEmpty(level))
            level = embeddedLevel;

        logs.Add(new FirehoseResponseLog(
            Enum.TryParse(level, true, out FirehoseLogLevel parsedLevel)
                ? parsedLevel
                : FirehoseLogLevel.Other,
            message));
    }
}
