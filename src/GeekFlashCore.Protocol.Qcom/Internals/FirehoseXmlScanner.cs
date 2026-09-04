using System.Net;
using System.Text;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal static class FirehoseXmlScanner
{
    public static bool TryReadNextElement(
        ReadOnlySpan<byte> xml,
        out ReadOnlySpan<byte> tagName,
        out Dictionary<string, string>? attributes,
        out ReadOnlySpan<byte> remaining)
    {
        tagName = default;
        attributes = null;
        remaining = default;

        int searchStart = 0;
        while (searchStart < xml.Length)
        {
            int relativeStart = xml[searchStart..].IndexOf((byte)'<');
            if (relativeStart < 0)
                return false;

            int tagStart = searchStart + relativeStart;
            int nameStart = tagStart + 1;
            if (nameStart >= xml.Length)
                return false;
            if (xml[nameStart] is (byte)'/' or (byte)'?' or (byte)'!')
            {
                searchStart = nameStart + 1;
                continue;
            }

            int nameEnd = nameStart;
            while (nameEnd < xml.Length &&
                   xml[nameEnd] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'>' or (byte)'/'))
                nameEnd++;

            int tagEnd = FindTagEnd(xml, nameEnd);
            if (tagEnd < 0)
                return false;

            tagName = xml[nameStart..nameEnd];
            attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ParseAttributes(xml[nameEnd..tagEnd], attributes);
            remaining = xml[(tagEnd + 1)..];
            return true;
        }

        return false;
    }

    public static string GetAttributeString(Dictionary<string, string>? attributes, string name) =>
        attributes is not null && attributes.TryGetValue(name, out string? value) ? value : string.Empty;

    public static void SplitLogValue(string value, out string level, out string message)
    {
        level = "Other";
        message = value;
        if (string.IsNullOrEmpty(value))
            return;

        int colon = value.IndexOf(':');
        if (colon is > 0 and <= 7)
        {
            string prefix = value[..colon].Trim();
            if (prefix.Equals("INFO", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("WARN", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("WARNING", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("DEBUG", StringComparison.OrdinalIgnoreCase))
            {
                level = prefix.Equals("WARNING", StringComparison.OrdinalIgnoreCase)
                    ? "Warn"
                    : prefix;
                message = value[(colon + 1)..].TrimStart();
                return;
            }
        }

        int bracketStart = value.IndexOf('[');
        int bracketEnd = value.IndexOf(']');
        if (bracketStart < 0 || bracketEnd <= bracketStart)
            return;

        level = value[(bracketStart + 1)..bracketEnd];
        message = value[(bracketEnd + 1)..].TrimStart();
    }

    private static int FindTagEnd(ReadOnlySpan<byte> xml, int start)
    {
        byte quote = 0;
        for (int index = start; index < xml.Length; index++)
        {
            byte current = xml[index];
            if (current is (byte)'"' or (byte)'\'')
                quote = quote == 0 ? current : quote == current ? (byte)0 : quote;
            else if (quote == 0 && current == (byte)'>')
                return index;
        }
        return -1;
    }

    private static void ParseAttributes(ReadOnlySpan<byte> section, Dictionary<string, string> attributes)
    {
        int position = 0;
        while (position < section.Length)
        {
            while (position < section.Length && IsWhitespace(section[position]))
                position++;
            if (position >= section.Length || section[position] == (byte)'/')
                break;

            int nameStart = position;
            while (position < section.Length &&
                   !IsWhitespace(section[position]) &&
                   section[position] != (byte)'=')
                position++;
            int nameEnd = position;
            while (position < section.Length &&
                   (IsWhitespace(section[position]) || section[position] == (byte)'='))
                position++;
            if (position >= section.Length || section[position] is not ((byte)'"' or (byte)'\''))
                break;

            byte quote = section[position++];
            int valueStart = position;
            while (position < section.Length && section[position] != quote)
                position++;
            if (position >= section.Length)
                break;

            string value = Encoding.UTF8.GetString(section[valueStart..position]);
            attributes[Encoding.UTF8.GetString(section[nameStart..nameEnd])] =
                value.Contains('&', StringComparison.Ordinal) ? WebUtility.HtmlDecode(value) : value;
            position++;
        }
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
