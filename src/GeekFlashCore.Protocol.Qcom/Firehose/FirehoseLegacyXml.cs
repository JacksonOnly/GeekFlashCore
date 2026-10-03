using System.Text.RegularExpressions;
using System.Xml;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

internal static partial class FirehoseLegacyXml
{
    internal const string DefaultNop = "<?xml version=\"1.0\" encoding=\"UTF-8\" chimerais=\"power\"?><data><nop value=\"Start Send Digest\"/></data>";

    internal static string ForValidation(string xml)
    {
        int end = xml.IndexOf("?>", StringComparison.Ordinal);
        if (!xml.StartsWith("<?xml", StringComparison.Ordinal) || end < 0) return xml;
        return SpecialDeclarationAttribute().Replace(xml[..end], "") + xml[end..];
    }

    internal static void ValidateNop(string xml)
    {
        string normalized = ForValidation(xml);
        FirehoseCommandExecutor.ValidateXml(normalized);
        using var reader = XmlReader.Create(new StringReader(normalized), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = FirehoseConstants.MaximumXmlPacketSize
        });
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.Name != "nop")
                throw new ArgumentException(Strings.Qcom_LegacyNopRequired, nameof(xml));
    }

    [GeneratedRegex("\\s+(?:chimerais|Bylaowang)\\s*=\\s*(?:\"power\"|'power')", RegexOptions.CultureInvariant)]
    private static partial Regex SpecialDeclarationAttribute();
}
