// SPDX-License-Identifier: AGPL-3.0-or-later
// Scatter fields and resize semantics: Shomy, penumbra-main scatter.rs, 2026.
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Parses MTK flat/nested YAML scatter records and XML scatters without YAML aliases or XML entities.</summary>
public static class MtkScatterManifestParser
{
    public const int MaximumCharacters = 4194304;
    public static MtkScatterManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is 0 or > MaximumCharacters)
            throw new MtkResourceException("scatter size");
        var records = text.AsSpan().TrimStart().StartsWith("<") ? Xml(text) : Yaml(text);
        List<MtkScatterPartition> parts = [];
        foreach (var record in records)
        {
            if (parts.Count >= 4096)
                throw new MtkResourceException("scatter count");
            string Required(string key) => record.TryGetValue(key, out var value) ? value : throw new MtkResourceException("scatter field");
            string name = Required("partition_name");
            if (name.Length is < 1 or > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
                throw new MtkResourceException("scatter name");
            string file = Required("file_name");
            string? path = file == "NONE" ? null : SafePath(file);
            bool download = Required("is_download") switch
            {
                "true" => true,
                "false" => false,
                _ => throw new MtkResourceException("scatter boolean")};
            string region = Required("region");
            var(kind, id) = region switch
            {
                "EMMC_BOOT1" => (MtkStorageKind.Emmc, 1u),
                "EMMC_BOOT2" => (MtkStorageKind.Emmc, 2u),
                "EMMC_BOOT1_BOOT2" => (MtkStorageKind.Emmc, 0u),
                "EMMC_USER" => (MtkStorageKind.Emmc, 8u),
                "EMMC_GP1" => (MtkStorageKind.Emmc, 4u),
                "EMMC_GP2" => (MtkStorageKind.Emmc, 5u),
                "EMMC_GP3" => (MtkStorageKind.Emmc, 6u),
                "EMMC_GP4" => (MtkStorageKind.Emmc, 7u),
                "UFS_LU0" => (MtkStorageKind.Ufs, 1u),
                "UFS_LU1" => (MtkStorageKind.Ufs, 2u),
                "UFS_LU2" => (MtkStorageKind.Ufs, 3u),
                "UFS_LU0_LU1" => (MtkStorageKind.Ufs, 1u),
                "NAND" or "NAND_WHOLE" => (MtkStorageKind.Nand, 8u),
                "NOR" => (MtkStorageKind.Nor, 8u),
                "SDMMC_USER" => (MtkStorageKind.Sdmmc, 8u),
                _ => throw new MtkResourceException("scatter region")};
            var operation = record.GetValueOrDefault("operation_type", "INVISIBLE") switch
            {
                "BOOTLOADERS" => MtkScatterOperation.Bootloaders,
                "" or "INVISIBLE" => MtkScatterOperation.Invisible,
                "UPDATE" => MtkScatterOperation.Update,
                "PROTECTED" => MtkScatterOperation.Protected,
                "BINREGION" => MtkScatterOperation.BinRegion,
                "RESERVED" => MtkScatterOperation.Reserved,
                "LOGIC" => MtkScatterOperation.Logic,
                "NEEDRESIZE" => MtkScatterOperation.NeedResize,
                "REBASE_RESIZE" => MtkScatterOperation.RebaseResize,
                _ => throw new MtkResourceException("scatter operation")};
            uint? host = record.TryGetValue("host", out var h) ? checked((uint)Number(h)) : null;
            var part = new MtkScatterPartition(name, path, download, kind, id, Number(Required("linear_start_addr")), Number(Required("partition_size")), operation, host);
            if (parts.Any(p => p.Storage == kind && p.RegionId == id && p.Host == host && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new MtkResourceException("scatter duplicate partition");
            parts.Add(part);
        }

        if (parts.Count == 0)
            throw new MtkResourceException("scatter empty");
        return new(Array.AsReadOnly(parts.ToArray()));
    }

    private static string SafePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.Length is < 1 or > 1024 || normalized.StartsWith('/') || normalized.Contains(':') || normalized.Any(char.IsControl) || normalized.Split('/').Any(p => p is "" or "." or ".."))
            throw new MtkResourceException("scatter file path");
        return normalized;
    }

    private static ulong Number(string value)
    {
        bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!ulong.TryParse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out ulong number))
            throw new MtkResourceException("scatter number");
        return number;
    }

    private static List<Dictionary<string, string>> Xml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumCharacters, MaxCharactersFromEntities = 0 });
        try
        {
            var root = XElement.Load(reader);
            if (root.Name != "root" || root.Descendants().Count() > 100000 || root.DescendantsAndSelf().Any(e => e.Name.Namespace != XNamespace.None || e.Ancestors().Count() > 12))
                throw new MtkResourceException("scatter XML structure");
            List<Dictionary<string, string>> result = [];
            foreach (var element in root.Descendants("partition_index"))
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in element.Elements())
                {
                    if (field.HasElements || !fields.TryAdd(field.Name.LocalName, field.Value.Trim()))
                        throw new MtkResourceException("scatter XML duplicate field");
                }

                result.Add(fields);
            }

            return result;
        }
        catch (XmlException)
        {
            throw new MtkResourceException("scatter XML structure");
        }
    }

    private static List<Dictionary<string, string>> Yaml(string text)
    {
        List<Dictionary<string, string>> result = [];
        Stack<YamlMapping> mappings = [];
        void Complete(int indent)
        {
            while (mappings.TryPeek(out var mapping) && mapping.Indent >= indent)
            {
                mappings.Pop();
                if (!mapping.IsPartition) continue;
                if (mapping.HasNestedEntries)
                    throw new MtkResourceException(Localization.Strings.ScatterYamlStructure);
                if (result.Count >= 4096)
                    throw new MtkResourceException("scatter count");
                result.Add(mapping.Fields);
            }
        }
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains('\t'))
                throw new MtkResourceException("scatter YAML tab");
            int indent = line.Length - line.TrimStart(' ').Length;
            string item = line.Trim();
            if (item.Length == 0 || item.StartsWith('#'))
                continue;
            bool sequence = item.StartsWith("- ");
            if (sequence)
                item = item[2..].Trim();
            int colon = item.IndexOf(':');
            if (colon < 1)
                throw new MtkResourceException("scatter YAML field");
            string key = item[..colon].Trim();
            string value = Scalar(item[(colon + 1)..].Trim());
            Complete(indent);
            if (sequence)
            {
                if (mappings.TryPeek(out var parent))
                {
                    if (parent.IsPartition)
                        throw new MtkResourceException(Localization.Strings.ScatterYamlStructure);
                    parent.HasNestedEntries = true;
                }
                if (mappings.Count >= 12)
                    throw new MtkResourceException(Localization.Strings.ScatterYamlStructure);
                var mapping = new YamlMapping(indent);
                mapping.Add(key, value);
                mappings.Push(mapping);
            }
            else if (mappings.TryPeek(out var mapping))
            {
                if (indent != mapping.Indent + 2)
                {
                    if (mapping.IsPartition || IsPartitionField(key))
                        throw new MtkResourceException(Localization.Strings.ScatterYamlStructure);
                    mapping.HasNestedEntries = true;
                    continue;
                }
                mapping.Add(key, value);
            }
            else if (IsPartitionField(key))
                throw new MtkResourceException(Localization.Strings.ScatterYamlStructure);
        }
        Complete(0);
        return result;
    }

    private static bool IsPartitionField(string key) => key is "partition_index" or "partition_name" or
        "file_name" or "is_download" or "linear_start_addr" or "partition_size";

    private sealed class YamlMapping(int indent)
    {
        public int Indent { get; } = indent;
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
        public bool IsPartition { get; private set; }
        public bool HasNestedEntries { get; set; }
        public void Add(string key, string value)
        {
            if (Fields.Count >= 256)
                throw new MtkResourceException(Localization.Strings.ScatterYamlStructure);
            if (!Fields.TryAdd(key, value))
                throw new MtkResourceException("scatter YAML duplicate field");
            IsPartition |= IsPartitionField(key);
        }
    }

    private static string Scalar(string value)
    {
        if (value.StartsWith('"') || value.StartsWith('\''))
        {
            char quote = value[0];
            int end = value.LastIndexOf(quote);
            if (end < 1 || value[(end + 1)..].Trim()is { Length: > 0 } rest && !rest.StartsWith('#'))
                throw new MtkResourceException("scatter YAML quote");
            value = value[1..end];
            if (quote == '\'')
                value = value.Replace("''", "'");
            else if (value.Contains('\\'))
                throw new MtkResourceException("scatter YAML escape");
            return value;
        }

        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
            value = value[..comment].TrimEnd();
        if (value.StartsWith('&') || value.StartsWith('*') || value.StartsWith('!') || value.StartsWith('[') || value.StartsWith('{') || value is "|" or ">")
            throw new MtkResourceException("scatter YAML unsupported value");
        return value;
    }
}

/// <summary>Resolves region aliases and tail-relative reserved ranges against observed storage, before writing.</summary>
public static class MtkScatterPlanBuilder
{
    public static MtkScatterPlan Create(MtkScatterManifest manifest, MtkStorageInfo storage, long generation)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(storage);
        if (manifest.Partitions is null || manifest.Partitions.Count is < 1 or > 4096 || manifest.Partitions.Any(p => p is null || !Enum.IsDefined(p.Storage) || !Enum.IsDefined(p.Operation)))
            throw new MtkResourceException("scatter manifest entries");
        var selected = manifest.Partitions.Where(p => p.Storage == storage.Kind).ToArray();
        if (selected.Length == 0)
            throw new MtkResourceException("scatter storage mismatch");
        if (selected.Any(p => p.Host is> 0))
            throw new MtkCapabilityException("multi-host scatter requires separate geometry");
        // A selected download must never disappear because its physical region or file is absent.
        // Region zero explicitly mirrors both boot regions; one observed region is not enough.
        foreach (var part in selected.Where(p => p.Download))
        {
            if (part.FileName is null)
                throw new MtkResourceException("scatter selected image");
            if (part.Operation == MtkScatterOperation.Logic)
                throw new MtkCapabilityException("logical scatter download");
            bool present = part.RegionId == 0 ? part.Storage is MtkStorageKind.Emmc or MtkStorageKind.Ufs && storage.Regions.Any(r => r.Kind == part.Storage && r.WireId == 1) && storage.Regions.Any(r => r.Kind == part.Storage && r.WireId == 2) : storage.Regions.Any(r => r.Kind == part.Storage && r.WireId == part.RegionId);
            if (!present)
                throw new MtkResourceException("scatter selected storage region");
        }

        List<MtkScatterPlannedPartition> resolved = [];
        foreach (var region in storage.Regions)
        {
            var source = manifest.Partitions.Where(p => p.Storage == region.Kind && (p.RegionId == region.WireId || p.RegionId == 0 && region.WireId is 1 or 2) && p.Operation != MtkScatterOperation.Logic && p.Host is null or 0).ToArray();
            if (source.Any(p => p.Operation == MtkScatterOperation.RebaseResize))
                throw new MtkCapabilityException("scatter rebase requires separate host geometry");
            ulong[] offsets = source.Select(p => p.Offset).ToArray(), sizes = source.Select(p => p.Length).ToArray();
            ulong next = (ulong)region.Length;
            for (int i = source.Length - 1; i >= 0; i--)
            {
                var part = source[i];
                bool tail = part.Operation == MtkScatterOperation.Reserved && ((part.Offset & 0xffff0000) == 0xffff0000 || (part.Offset & 0xffff000000000000) == 0xffff000000000000);
                if (tail)
                {
                    if (sizes[i] > next)
                        throw new MtkResourceException("scatter reserved capacity");
                    offsets[i] = next - sizes[i];
                }
                else if (part.Operation == MtkScatterOperation.NeedResize || sizes[i] == 0)
                {
                    if (offsets[i] >= next)
                        throw new MtkResourceException("scatter resize");
                    sizes[i] = next - offsets[i];
                }

                next = offsets[i];
            }

            for (int i = 0; i < source.Length; i++)
            {
                if (sizes[i] == 0 || offsets[i] > (ulong)region.Length || sizes[i] > (ulong)region.Length - offsets[i] || offsets[i] % (uint)region.BlockSize != 0 || sizes[i] % (uint)region.BlockSize != 0)
                    throw new MtkResourceException("scatter range");
                var range = new MtkFlashRange(region.WireId, (long)offsets[i], (long)sizes[i]);
                resolved.Add(new(source[i].Name, source[i].FileName, source[i].Download, range, source[i].Operation));
            }
        }

        foreach (var group in resolved.GroupBy(p => p.Range.RegionId))
        {
            var sorted = group.OrderBy(p => p.Range.Offset).ToArray();
            for (int i = 1; i < sorted.Length; i++)
                if (sorted[i - 1].Range.Offset + sorted[i - 1].Range.Length > sorted[i].Range.Offset)
                    throw new MtkResourceException("scatter overlap");
        }

        if (resolved.Count == 0)
            throw new MtkResourceException("scatter storage mismatch");
        return new(generation, Array.AsReadOnly(resolved.ToArray()));
    }
}
