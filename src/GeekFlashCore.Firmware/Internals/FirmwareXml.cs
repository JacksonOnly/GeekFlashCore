using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal static class FirmwareXml
{
    private const string ProgramAttributes = "SECTOR_SIZE_IN_BYTES file_sector_offset filename label num_partition_sectors partofsingleimage physical_partition_number readbackverify size_in_KB sparse start_byte_hex start_sector";
    private const string PatchAttributes = "SECTOR_SIZE_IN_BYTES byte_offset filename physical_partition_number size_in_bytes start_sector value what";
    internal static XElement Parse(ParseContext c, ReadOnlySpan<byte> bytes)
    {
        string text = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0', ' ', '\r', '\n', '\t');
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = c.Options.MaximumMetadataBytes, IgnoreComments = true };
        using (var reader = XmlReader.Create(new StringReader(text), settings))
        {
            int elements = 0;
            while (reader.Read())
            {
                c.Check(); if (reader.Depth > 64 || reader.AttributeCount > 64 || reader.Name.Length > 128 || reader.NamespaceURI.Length != 0) throw new InvalidDataException(Strings.InvalidMetadata);
                if (reader.NodeType == XmlNodeType.Element) c.Limit(++elements, c.Options.MaximumSegments);
            }
        }
        using var final = XmlReader.Create(new StringReader(text), settings); return XElement.Load(final);
    }
    internal static XElement ParseOfp(ParseContext c, ReadOnlySpan<byte> bytes)
    {
        // Some Qualcomm OFP lengths include one opaque terminator after the complete XML root.
        static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
        { while (!value.IsEmpty && value[^1] is 0 or 32 or 13 or 10 or 9) value = value[..^1]; return value; }
        if (bytes.Length > 1 && !Trim(bytes).EndsWith("</ProFile>"u8) && Trim(bytes[..^1]).EndsWith("</ProFile>"u8)) bytes = bytes[..^1];
        return Parse(c, bytes);
    }
    internal static long Number(XElement e, string attribute, long fallback = 0)
    {
        string? text = (string?)e.Attribute(attribute); if (text is null) return fallback;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value)) throw new InvalidDataException(Strings.InvalidMetadata);
        return value;
    }
    internal static XElement? Section(XElement root, string name)
    {
        XElement[] matches = root.Elements(name).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidDataException(Strings.InvalidMetadata);
        return matches.FirstOrDefault();
    }
    internal static long Offset(XElement e, int page)
    {
        long sectors = (string?)e.Attribute("FileOffsetInSrc") == "-1" ? -1 : Number(e, "FileOffsetInSrc", -1);
        if (sectors < 0) sectors = Number(e, "SizeInSectorInSrc");
        return checked(sectors * page);
    }
    internal static void OfpEntries(ParseContext c, XElement root, int page, byte[] key, byte[] iv)
    {
        var known = new Dictionary<string, (long Start, long Size, bool Encrypted)>(StringComparer.Ordinal);
        foreach (string section in new[] { "Sahara", "ProgramList", "Super", "Config", "Provision", "ChainedTableOfDigests", "DigestsToSign" })
        {
            foreach (var e in Section(root, section)?.Elements() ?? Enumerable.Empty<XElement>())
            {
                string? name = (string?)e.Attribute("filename") ?? (string?)e.Attribute("Path");
                if (string.IsNullOrWhiteSpace(name)) continue;
                long size = Number(e, "SizeInByteInSrc"), start = Offset(e, page); bool encrypted = section is not ("ChainedTableOfDigests" or "DigestsToSign");
                if (section == "ProgramList" && size == 0 && (string?)e.Attribute("label") == "super" && Section(root, "Super") is not null) continue;
                name = FirmwarePath.Normalize(name); var layout = (start, size, encrypted);
                if (known.TryGetValue(name, out var previous)) { if (previous != layout) throw new InvalidDataException(Strings.AmbiguousEntry); continue; }
                known.Add(name, layout);
                if (encrypted) c.Encrypted(name, start, size, Math.Min(size, 0x40000), key, iv); else c.Slice(name, start, size);
            }
        }
        bool hasSuper = OfpSuperMapper.Ofp(c, root);
        if (Section(root, "ProgramList") is { } programs)
        {
            Script(c, "rawprogram.xml", programs.Elements("program").Select(e =>
            {
                if (!hasSuper || (string?)e.Attribute("label") != "super" || !string.IsNullOrWhiteSpace((string?)e.Attribute("filename"))) return e;
                var fixedProgram = new XElement(e); fixedProgram.SetAttributeValue("filename", "super.img"); return fixedProgram;
            }), false);
        }
        if (Section(root, "Super") is { } super) Script(c, "rawprogram_super.xml", super.Elements("program"), false);
        if (Section(root, "PatchList") is { } patches) Script(c, "patch.xml", patches.Elements("patch"), true);
    }
    internal static void Script(ParseContext c, string name, IEnumerable<XElement> commands, bool patch)
    {
        if (!c.GeneratedScripts.Add(name)) throw new InvalidDataException(Strings.InvalidMetadata);
        // Existing container files take precedence over a generated document with the same name.
        if (c.Entries.Any(e => e.Name == name)) return;
        var allowed = (patch ? PatchAttributes : ProgramAttributes).Split(' ').ToHashSet(StringComparer.Ordinal);
        var root = new XElement(patch ? "patches" : "data");
        foreach (var original in commands)
        {
            c.Check(); var e = new XElement(patch ? "patch" : "program");
            foreach (var a in original.Attributes()) if (allowed.Contains(a.Name.LocalName)) e.Add(new XAttribute(a));
            root.Add(e);
        }
        byte[] bytes = Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting)); c.Limit(bytes.Length, c.Options.MaximumMetadataBytes); c.Virtual(name, bytes);
    }
}
