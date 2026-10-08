using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal static class MtkXmlCodec
{
    private static readonly Dictionary<string, string[]> Arguments = new(StringComparer.Ordinal)
    {
        [MtkXmlCommand.SetRuntimeParameter] = ["checksum_level", "battery_exist", "da_log_level", "log_channel", "system_os", "initialize_dram"],
        [MtkXmlCommand.HostSupportedCommands] = ["host_capability"],
        [MtkXmlCommand.NotifyInitHw] = [],
        [MtkXmlCommand.SetHostInfo] = ["info"],
        [MtkXmlCommand.BootTo] = ["at_address", "jmp_address", "source_file"],
        [MtkXmlCommand.GetHwInfo] = ["target_file"],
        [MtkXmlCommand.ReadPartitionTable] = ["target_file"],
        [MtkXmlCommand.GetSysProperty] = ["key", "target_file"],
        [MtkXmlCommand.ReadRegister] = ["bit_width", "base_address", "target_file"],
        [MtkXmlCommand.WriteRegister] = ["bit_width", "base_address", "source_file"],
        [MtkXmlCommand.SecurityGetDevFwInfo] = ["target_file"],
        [MtkXmlCommand.SecuritySetFlashPolicy] = ["source_file"],
        [MtkXmlCommand.SecuritySetAllinoneSignature] = ["source_file"],
        ["EXP-PATCH-MEM"] = ["address", "length"],
        ["EXP-CALL-FUNC"] = ["address"],
        [MtkXmlCommand.ReadEfuse] = ["target_file"],
        [MtkXmlCommand.WriteEfuse] = ["source_file"],
        [MtkXmlCommand.ReadPartition] = ["partition","target_file"],
        [MtkXmlCommand.WritePartition] = ["partition","source_file"],
        [MtkXmlCommand.ErasePartition] = ["partition"],
        [MtkXmlCommand.FlashUpdate] = ["source_file","path_separator","backup_folder"],
        [MtkXmlCommand.ReadFlash] = ["partition", "target_file", "length", "offset"],
        [MtkXmlCommand.WriteFlash] = ["partition", "source_file", "offset"],
        [MtkXmlCommand.EraseFlash] = ["partition", "length", "offset"],
        [MtkXmlCommand.Reboot] = ["action"],
        [MtkXmlCommand.SetBootMode] = ["mode", "connect_type", "mobile_log", "adb"],
        [MtkXmlCommand.ExtAck] = [],
        [MtkXmlCommand.ExtDaCtx] = ["sej_base", "tzcc_base", "ssr_base", "da2_base", "da2_size", "storage", "usb_log"],
        [MtkXmlCommand.ExtReadMem] = ["address", "length"],
        [MtkXmlCommand.ExtWriteMem] = ["address", "length"],
        [MtkXmlCommand.ExtKeyDerive] = ["key_type"],
        [MtkXmlCommand.ExtSej] = ["encrypt", "ac", "length"],
        [MtkXmlCommand.ExtRpmbInit] = ["partition", "key"],
        [MtkXmlCommand.ExtRpmbRead] = ["partition", "start_sector", "sectors_count"],
        [MtkXmlCommand.ExtRpmbWrite] = ["partition", "start_sector", "sectors_count"]
    };
    public static byte[] Create(string command, IReadOnlyDictionary<string, string> parameters)
    {
        string[]? variant=command switch
        {
            MtkXmlCommand.ExtKeyDerive when parameters.Count==4=>["key_type","key_length","label","salt"],
            MtkXmlCommand.ExtSej when parameters.Count==6=>["encrypt","ac","length","cbc","key_id","key_size"],
            _=>null
        };
        if(variant is not null)return CreateValidated(command,parameters,variant);
        if (!Arguments.TryGetValue(command, out var allowed) || parameters.Count != allowed.Length || parameters.Any(p =>
            !allowed.Contains(p.Key, StringComparer.Ordinal) || p.Value is null ||
            // The all-in-one signature source_file carries the heap-shaping filename (8192 bound).
            p.Value.Length > (command == MtkXmlCommand.SecuritySetAllinoneSignature ? 8192 : 4096)))
            throw new MtkCapabilityException("XML command/parameters");
        return CreateValidated(command,parameters,allowed);
    }
    private static byte[] CreateValidated(string command,IReadOnlyDictionary<string,string> parameters,string[] allowed)
    {
        if(parameters.Count!=allowed.Length || parameters.Any(p=>!allowed.Contains(p.Key,StringComparer.Ordinal) || p.Value is null || p.Value.Length>4096))throw new MtkCapabilityException("XML command/parameters");
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("da");
            writer.WriteElementString("version", command == MtkXmlCommand.SetRuntimeParameter ? "1.1" : "1.0");
            writer.WriteElementString("command", MtkXmlCommand.Prefix + command);
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
    public static XElement Parse(ReadOnlySpan<byte> bytes, int limit, bool allowPropertyKey = false, bool allowPartitionVersion = false)
    {
        if (bytes.Length == 0 || bytes.Length > limit)
            throw new MtkResourceException("XML length");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0'); }
        catch (DecoderFallbackException) { throw new MtkResourceException("XML encoding"); }
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
                    !(allowPropertyKey && e.Name == "item" && a.Name == "key" && a.Value.Length <= 128) &&
                    !(allowPartitionVersion && e == root && e.Name == "partition_table" && a.Name == "version" && a.Value == "1.0"))))
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
