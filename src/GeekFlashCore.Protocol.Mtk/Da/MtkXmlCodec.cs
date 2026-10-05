using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal static class MtkXmlCodec
{
    private static readonly Dictionary<string, string[]> Arguments = new(StringComparer.Ordinal)
    {
        ["SET-RUNTIME-PARAMETER"] = ["checksum_level", "battery_exist", "da_log_level", "log_channel", "system_os", "initialize_dram"],
        ["HOST-SUPPORTED-COMMANDS"] = ["host_capability"],
        ["NOTIFY-INIT-HW"] = [],
        ["SET-HOST-INFO"] = ["info"],
        ["BOOT-TO"] = ["at_address", "jmp_address", "source_file"],
        ["GET-HW-INFO"] = ["target_file"],
        ["GET-SYS-PROPERTY"] = ["key", "target_file"],
        ["SECURITY-GET-DEV-FW-INFO"] = ["target_file"],
        ["SECURITY-SET-FLASH-POLICY"] = ["source_file"],
        ["READ-FLASH"] = ["partition", "target_file", "length", "offset"],
        ["WRITE-FLASH"] = ["partition", "source_file", "offset"],
        ["ERASE-FLASH"] = ["partition", "length", "offset"],
        ["REBOOT"] = ["action"],
        ["SET-BOOT-MODE"] = ["mode", "connect_type", "mobile_log", "adb"],
        ["EXT-ACK"] = [],
        ["EXT-DA-CTX"] = ["sej_base", "tzcc_base", "ssr_base", "da2_base", "da2_size", "storage", "usb_log"],
        ["EXT-READ-MEM"] = ["address", "length"],
        ["EXT-WRITE-MEM"] = ["address", "length"],
        ["EXT-KEY-DERIVE"] = ["key_type"],
        ["EXT-SEJ"] = ["encrypt", "ac", "length"],
        ["EXT-RPMB-INIT"] = ["partition", "key"],
        ["EXT-RPMB-READ"] = ["partition", "start_sector", "sectors_count"],
        ["EXT-RPMB-WRITE"] = ["partition", "start_sector", "sectors_count"]
    };
    public static byte[] Create(string command, IReadOnlyDictionary<string, string> parameters)
    {
        if (!Arguments.TryGetValue(command, out var allowed) || parameters.Count != allowed.Length || parameters.Any(p =>
            !allowed.Contains(p.Key, StringComparer.Ordinal) || p.Value is null || p.Value.Length > 4096))
            throw new MtkCapabilityException("XML command/parameters");
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("da");
            writer.WriteElementString("version", command == "SET-RUNTIME-PARAMETER" ? "1.1" : "1.0");
            writer.WriteElementString("command", "CMD:" + command);
            writer.WriteStartElement("arg");
            foreach (var p in parameters.Where(p => p.Key != "initialize_dram"))
                writer.WriteElementString(p.Key, p.Value);
            writer.WriteEndElement();
            if (parameters.TryGetValue("initialize_dram", out string? init))
            {
                writer.WriteStartElement("adv");
                writer.WriteElementString("initialize_dram", init);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        return Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + text + "\0");
    }
    public static XElement Parse(ReadOnlySpan<byte> bytes, int limit, bool allowPropertyKey = false)
    {
        if (bytes.Length == 0 || bytes.Length > limit)
            throw new MtkResourceException("XML length");
        string text = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limit,
            MaxCharactersFromEntities = 0
        });
        try
        {
            XElement root = XElement.Load(reader);
            if (root.DescendantsAndSelf().Count() > 1024 || root.DescendantsAndSelf().Any(e =>
                e.Ancestors().Count() > 8 || e.Name.NamespaceName.Length != 0 || e.Attributes().Any(a =>
                    !allowPropertyKey || e.Name != "item" || a.Name != "key" || a.Value.Length > 128)))
                throw new MtkResourceException("XML structure");
            return root;
        }
        catch (XmlException) { throw new MtkResourceException("XML structure"); }
    }
    public static string Value(XElement root, string name)
    {
        var elements = root.DescendantsAndSelf(name).ToArray();
        if (elements.Length != 1 || elements[0].HasElements)
            throw new MtkResourceException("XML field " + name);
        return elements[0].Value;
    }
    public static ulong Number(string text)
    {
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!ulong.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.None,
            CultureInfo.InvariantCulture, out ulong number))
            throw new MtkResourceException("XML number");
        return number;
    }
}
