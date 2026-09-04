using System.Text;
using System.Xml;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors;

public sealed record ValidatedCustomCommand(string Name, string Xml, string RedactedForLog);

public static class CustomCommandValidator
{
    private const int MaximumAttributeCount = 64;
    private const int MaximumAttributeValueLength = 4096;

    public static ValidatedCustomCommand Validate(string xml, IVendorFirehoseStrategy strategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentNullException.ThrowIfNull(strategy);
        if (Encoding.UTF8.GetByteCount(xml) > FirehoseConstants.MaximumXmlPacketSize)
            throw new ArgumentException("The Firehose XML document exceeds the size limit.", nameof(xml));

        try
        {
            using var text = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(text, CreateSettings());
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 0 ||
                !reader.LocalName.Equals("data", StringComparison.Ordinal) ||
                reader.NamespaceURI.Length != 0 || reader.IsEmptyElement)
            {
                throw new XmlException("The Firehose XML root element must be data.");
            }

            string? commandName = null;
            string? redacted = null;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Depth != 1 || commandName is not null || reader.NamespaceURI.Length != 0)
                        throw new XmlException("The Firehose XML must contain one flat command element.");
                    commandName = reader.LocalName;
                    if (!strategy.AllowedCustomCommands.Contains(commandName))
                        throw new XmlException($"Command '{commandName}' is not allowed for {strategy.Vendor}.");
                    redacted = BuildRedactedCommand(reader, commandName);
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA &&
                         !string.IsNullOrWhiteSpace(reader.Value))
                {
                    throw new XmlException("Firehose command text content is not allowed.");
                }
            }

            if (commandName is null || redacted is null)
                throw new XmlException("The Firehose XML contains no command.");
            return new ValidatedCustomCommand(commandName, xml, redacted);
        }
        catch (XmlException exception)
        {
            throw new ArgumentException("The Firehose custom XML is invalid or not allowed.", nameof(xml), exception);
        }
    }

    private static XmlReaderSettings CreateSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        ConformanceLevel = ConformanceLevel.Document,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = FirehoseConstants.MaximumXmlPacketSize,
        MaxCharactersFromEntities = 0
    };

    private static string BuildRedactedCommand(XmlReader reader, string commandName)
    {
        var builder = new StringBuilder(128).Append('<').Append(commandName);
        if (reader.HasAttributes)
        {
            int count = 0;
            while (reader.MoveToNextAttribute())
            {
                if (++count > MaximumAttributeCount || reader.Value.Length > MaximumAttributeValueLength ||
                    reader.Prefix.Equals("xmlns", StringComparison.Ordinal) ||
                    reader.Name.Equals("xmlns", StringComparison.Ordinal))
                {
                    throw new XmlException("The Firehose command attributes exceed the safety limits.");
                }
                builder.Append(' ').Append(reader.Name).Append("=\"");
                AppendEscaped(builder, IsSensitive(reader.LocalName) ? "***" : reader.Value);
                builder.Append('"');
            }
            reader.MoveToElement();
        }
        return builder.Append(" />").ToString();
    }

    private static bool IsSensitive(string name) =>
        name.Equals("pk", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("blob", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("key", StringComparison.OrdinalIgnoreCase);

    private static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (char character in value)
        {
            string? entity = character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ => null
            };
            if (entity is null)
                builder.Append(character);
            else
                builder.Append(entity);
        }
    }
}
