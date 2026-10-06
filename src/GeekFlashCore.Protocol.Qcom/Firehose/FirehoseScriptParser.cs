using System.Globalization;
using System.Xml;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

internal sealed record FirehoseScriptElement(string Name, IReadOnlyDictionary<string, string> Attributes)
{
    internal string? Optional(string key) => Attributes.GetValueOrDefault(key);
    internal string Required(string key) => Optional(key) is { Length: > 0 } value
        ? value : throw new ArgumentException(Strings.FormatQcom_ScriptAttributeMissing(Name, key));
    internal ulong Number(string key, ulong? fallback = null) => Optional(key) is { } value
        ? FirehoseScriptParser.Evaluate(value, null) : fallback ?? FirehoseScriptParser.Evaluate(Required(key), null);
}

internal static class FirehoseScriptParser
{
    internal const int MaximumCharacters = 8 * 1024 * 1024;
    internal static IReadOnlyList<FirehoseScriptElement> Read(IDataSource source, bool patchOnly, CancellationToken ct)
    {
        if (source.Length is <= 0 or > MaximumCharacters) throw new ArgumentException(Strings.Qcom_ScriptInvalid);
        using Stream stream = source.OpenStream();
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters, MaxCharactersFromEntities = 0,
            IgnoreComments = true, IgnoreProcessingInstructions = true
        });
        try
        {
            ct.ThrowIfCancellationRequested();
            reader.MoveToContent();
            if (reader.NamespaceURI.Length != 0 || reader.AttributeCount != 0 ||
                !(reader.Name == "data" || patchOnly && reader.Name == "patches"))
                throw new XmlException();
            var entries = new List<FirehoseScriptElement>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Depth != 1 || reader.NamespaceURI.Length != 0 || entries.Count >= 16384 ||
                        reader.Name.Length > 128 || reader.AttributeCount > 64) throw new XmlException();
                    string name = reader.Name;
                    var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                    while (reader.MoveToNextAttribute())
                    {
                        if (reader.NamespaceURI.Length != 0 || reader.Name.Length > 128 || reader.Value.Length > 4096)
                            throw new XmlException();
                        attributes.Add(reader.Name, reader.Value);
                    }
                    reader.MoveToElement();
                    entries.Add(new(name, attributes));
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && !string.IsNullOrWhiteSpace(reader.Value))
                    throw new XmlException();
            }
            return entries;
        }
        catch (XmlException e) { throw new ArgumentException(Strings.Qcom_ScriptInvalid, nameof(source), e); }
    }

    // Qualcomm's trailing decimal dot is accepted; arbitrary device expressions are not.
    internal static ulong Evaluate(string expression, ulong? diskSectors)
    {
        if (expression.Length is 0 or > 256) throw new ArgumentException(Strings.Qcom_ScriptExpressionInvalid);
        ReadOnlySpan<char> text = expression.AsSpan().Trim();
        ulong result = 0; char operation = '+';
        while (!text.IsEmpty)
        {
            int end = text.IndexOfAny('+', '-');
            var operand = (end < 0 ? text : text[..end]).Trim();
            if (operand.IsEmpty) throw new ArgumentException(Strings.Qcom_ScriptExpressionInvalid);
            if (operand[^1] == '.') operand = operand[..^1];
            ulong value;
            if (operand.SequenceEqual("NUM_DISK_SECTORS"))
                value = diskSectors ?? throw new ArgumentException(Strings.Qcom_ScriptExpressionInvalid);
            else if (operand.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = ulong.Parse(operand[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            else value = ulong.Parse(operand, NumberStyles.None, CultureInfo.InvariantCulture);
            result = operation == '+' ? checked(result + value) : checked(result - value);
            if (end < 0) return result;
            operation = text[end]; text = text[(end + 1)..].Trim();
            if (text.IsEmpty) throw new ArgumentException(Strings.Qcom_ScriptExpressionInvalid);
        }
        throw new ArgumentException(Strings.Qcom_ScriptExpressionInvalid);
    }

    internal static string PatchValue(string value, ulong diskSectors, uint sectorSize, uint size)
    {
        string text = value.Trim();
        if (text.StartsWith("CRC32(", StringComparison.Ordinal) && text.EndsWith(')'))
        {
            string[] parts = text[6..^1].Split(',');
            if (parts.Length != 2 || size != 4) throw new ArgumentException(Strings.Qcom_ScriptExpressionInvalid);
            ulong start = Evaluate(parts[0], diskSectors), length = Evaluate(parts[1], diskSectors);
            if (start >= diskSectors || length == 0 || length > checked((diskSectors - start) * sectorSize))
                throw new ArgumentException(Strings.Qcom_StorageRangeOutsideDevice);
            return $"CRC32({start.ToString(CultureInfo.InvariantCulture)},{length.ToString(CultureInfo.InvariantCulture)})";
        }
        ulong number = Evaluate(text, diskSectors);
        if (size < 8 && number >= (1UL << checked((int)size * 8)))
            throw new ArgumentException(Strings.Qcom_ScriptExpressionInvalid);
        return number.ToString(CultureInfo.InvariantCulture);
    }
}
