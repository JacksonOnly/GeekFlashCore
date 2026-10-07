using System.Globalization;
using System.Xml;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Internals;

// This is a probe outcome, never an ACK or proof of authentication.
internal readonly record struct FirehoseProbeResult(FirehoseResponse Response, bool AwaitingSignedTable);

internal static class FirehoseSignedTableWait
{
    internal static bool IsAnnouncement(FirehoseResponseLog log)
    {
        const string marker = "VIP is enabled, receiving the signed table";
        string message = log.Message.Trim();
        if (!message.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return false;
        ReadOnlySpan<char> suffix = message.AsSpan(marker.Length);
        return suffix.IsEmpty || suffix.StartsWith(" of size ", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(suffix[9..], NumberStyles.None, CultureInfo.InvariantCulture, out uint size) && size > 0;
    }

    internal static void ValidateLogPacket(ReadOnlySpan<byte> packet)
    {
        // The fast response scanner tolerates vendor XML. A state transition needs
        // a complete document consisting exclusively of data/log elements.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = FirehoseConstants.MaximumXmlPacketSize,
            MaxCharactersFromEntities = 0,
            IgnoreWhitespace = true
        };
        try
        {
            using var stream = new MemoryStream(packet.ToArray(), writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.Name != "data" || reader.IsEmptyElement)
                throw new XmlException();
            while (reader.Read())
            {
                if ((reader.NodeType == XmlNodeType.Element &&
                    (reader.Depth != 1 || reader.Name != "log" || !reader.IsEmptyElement)) ||
                    reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    throw new XmlException();
            }
        }
        catch (XmlException exception)
        {
            throw new FirehoseProtocolException(Strings.Qcom_SignedTableAnnouncementInvalid, exception);
        }
    }
}
