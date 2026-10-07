using System.Globalization;
using System.Text;
using System.Text.Json;
using GeekFlashCore.Android.Lp;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.Firmware.Localization;
using GeekFlashCore.ImageFormats.Abstractions;

namespace GeekFlashCore.Firmware.Internals;

internal sealed record OplusSuperDefinition(LpSuperImageLayout Layout, string ImageName,
    IReadOnlyDictionary<string, Func<CancellationToken, Stream>> Images)
{
    internal static OplusSuperDefinition Parse(FirmwarePackage package, string name, CancellationToken ct)
    {
        try { return ParseCore(package, name, ct); }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or OverflowException)
        { throw new InvalidDataException(Strings.SuperDefinitionInvalid, e); }
    }
    private static OplusSuperDefinition ParseCore(FirmwarePackage package, string name, CancellationToken ct)
    {
        var options = package.Options; int maximum = Math.Min(options.MaximumMetadataBytes, 4 * 1024 * 1024);
        using var json = JsonDocument.Parse(Read(package.GetEntry(name), maximum, ct), new() { MaxDepth = 32 });
        int nodes = 0; CheckJson(json.RootElement); var root = json.RootElement;
        string directory = name.Contains('/') ? name[..name.LastIndexOf('/')] : "";
        string prefix = directory.Equals("META", StringComparison.OrdinalIgnoreCase) ? "" : directory.EndsWith("/META", StringComparison.OrdinalIgnoreCase) ? directory[..^4] : directory.Length == 0 ? "" : throw Invalid();
        var devices = Array(root, "block_devices").Take(2).ToArray(); if (devices.Length != 1) throw Invalid();
        JsonElement device = devices[0]; string deviceName = Text(device, "name", true)!;
        long capacity = Number(device, "size"); uint block = checked((uint)Number(device, "block_size", 4096)), alignment = checked((uint)Number(device, "alignment", 1048576)), alignmentOffset = checked((uint)Number(device, "alignment_offset"));
        if (Number(device, "flags") != 0) throw Invalid();
        var groups = new List<LpSuperGroupDefinition>(); var groupNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in Array(root, "groups"))
        {
            string group = Text(g, "name", true)!; if (!groupNames.Add(group) || Number(g, "flags") != 0) throw Invalid();
            groups.Add(new(group, checked((ulong)Number(g, "maximum_size"))));
        }
        var partitions = new List<LpSuperPartitionDefinition>(); var images = new Dictionary<string, Func<CancellationToken, Stream>>(StringComparer.Ordinal);
        var partitionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Array(root, "partitions"))
        {
            ct.ThrowIfCancellationRequested(); string partition = Text(p, "name", true)!; long size = Number(p, "size");
            if (!partitionNames.Add(partition) || Boolean(p, "is_dynamic") == false) throw Invalid();
            partitions.Add(new(partition, size, Text(p, "group_name") ?? "default", (LpPartitionAttributes)checked((uint)Number(p, "attributes", 1))));
            if (size != 0)
            {
                string path = Text(p, "path", true)!; FirmwareEntry entry = ResolveReference(package, prefix + FirmwarePath.Normalize(path), ct);
                images.Add(partition, token => entry.OpenStream(token));
            }
        }
        if (partitions.Count == 0) throw Invalid();
        JsonElement meta = root.TryGetProperty("super_meta", out var m) && m.ValueKind != JsonValueKind.Null ? m : default;
        uint metadataSize = checked((uint)(meta.ValueKind == JsonValueKind.Object ? Number(meta, "size", 65536) : 65536));
        uint slots = checked((uint)Number(root, "metadata_slots", 2)); bool? virtualAb = Boolean(root, "virtual_ab");
        string infoName = prefix + "META/dynamic_partitions_info.txt";
        if (package.Entries.Any(e => e.Name == infoName))
        {
            string info = new UTF8Encoding(false, true).GetString(Read(package.GetEntry(infoName), Math.Min(maximum, 65536), ct)); bool? inferred = null;
            foreach (string line in info.Split('\n'))
            {
                var fields = line.Trim().Split('=', 2); if (fields[0] != "virtual_ab") continue;
                if (fields.Length != 2 || inferred is not null || fields[1] is not ("true" or "false")) throw Invalid(); inferred = fields[1] == "true";
            }
            if (virtualAb is not null && inferred is not null && virtualAb != inferred) throw Invalid(); virtualAb ??= inferred;
        }
        var limits = ImageReadLimits.Default with { MaxLpMetadataBytes = maximum, MaxDecodedLpMetadataBytes = Math.Min(options.MaximumMetadataBytes, 16 * 1024 * 1024), MaxLpTableEntries = Math.Min(options.MaximumSegments, 262144) };
        LpSuperImageLayout layout;
        if (meta.ValueKind == JsonValueKind.Object && Text(meta, "path") is { Length: > 0 } metadataPath)
        {
            FirmwareEntry entry = ResolveReference(package, prefix + FirmwarePath.Normalize(metadataPath), ct);
            if (entry.Length > maximum) throw Invalid(); using Stream stream = entry.OpenStream(ct);
            layout = LpSuperImageLayout.ReadMetadataBlob(stream, limits, ct); var physical = layout.BlockDevices.Span[0];
            if (layout.Length != capacity || physical.RawPartitionName != deviceName || physical.Alignment != alignment || physical.AlignmentOffset != alignmentOffset ||
                layout.Geometry.LogicalBlockSize != block || layout.Geometry.MetadataMaxSize != metadataSize ||
                root.TryGetProperty("metadata_slots", out _) && layout.Geometry.MetadataSlotCount != slots || virtualAb is not null && ((layout.Header.Flags & 1) != 0) != virtualAb)
                throw Invalid();
            if (layout.Groups.Length != groups.Count || layout.Partitions.Length != partitions.Count) throw Invalid();
            var expectedGroups = groups.ToDictionary(g => g.Name, StringComparer.Ordinal);
            var expectedPartitions = partitions.ToDictionary(p => p.Name, StringComparer.Ordinal);
            foreach (var g in layout.Groups.Span)
            { ct.ThrowIfCancellationRequested(); if (!expectedGroups.TryGetValue(g.RawName, out var j) || j.MaximumSize != g.MaximumSize) throw Invalid(); }
            foreach (var p in layout.Partitions.Span)
            {
                ct.ThrowIfCancellationRequested();
                if (!expectedPartitions.TryGetValue(p.RawName, out var j) || j.Size != p.LogicalSize || j.Attributes != p.Attributes || j.GroupName != layout.Groups.Span[(int)p.GroupIndex].RawName) throw Invalid();
            }
        }
        else layout = LpSuperImageLayout.Create(new() { DeviceSize = capacity, DeviceName = deviceName, Geometry = new(metadataSize, slots, block), Alignment = alignment, AlignmentOffset = alignmentOffset, VirtualAb = virtualAb ?? false }, partitions, groups, limits, ct);
        return new(layout, prefix + "IMAGES/" + deviceName + ".img", images);

        void CheckJson(JsonElement element)
        {
            ct.ThrowIfCancellationRequested(); if (++nodes > options.MaximumSegments) throw Invalid();
            if (element.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { if (property.Name.Length > 128 || !keys.Add(property.Name)) throw Invalid(); CheckJson(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) CheckJson(value);
            else if (element.ValueKind == JsonValueKind.String && element.GetString()!.Length > 4096) throw Invalid();
        }
    }
    private static IEnumerable<JsonElement> Array(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array) throw Invalid();
        return value.EnumerateArray();
    }
    private static string? Text(JsonElement root, string name, bool required = false)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) { if (required) throw Invalid(); return null; }
        if (value.ValueKind != JsonValueKind.String) throw Invalid(); string text = value.GetString()!;
        if (text.Any(char.IsControl) || required && string.IsNullOrWhiteSpace(text)) throw Invalid(); return text;
    }
    private static long Number(JsonElement root, string name, long fallback = 0)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Invalid(); if (!root.TryGetProperty(name, out var value)) return fallback;
        bool valid = value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out long result) : long.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null, NumberStyles.None, CultureInfo.InvariantCulture, out result);
        if (!valid || result < 0) throw Invalid(); return result;
    }
    private static bool? Boolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid() };
    }
    private static byte[] Read(FirmwareEntry entry, int maximum, CancellationToken ct)
    {
        if (entry.Length > maximum) throw Invalid(); byte[] bytes = new byte[checked((int)entry.Length)]; using var stream = entry.OpenStream(ct); stream.ReadExactly(bytes); ct.ThrowIfCancellationRequested(); return bytes;
    }
    private static FirmwareEntry ResolveReference(FirmwarePackage package, string name, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int depth = 0; depth <= 8; depth++)
        {
            package.Check(ct); if (!visited.Add(name)) throw new InvalidDataException(Strings.SuperReferenceInvalid); FirmwareEntry entry = package.GetEntry(name);
            if (entry.Length is < 1 or > 4096) return entry;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(Read(entry, Math.Min(4096, package.Options.MaximumMetadataBytes), ct)).Trim(' ', '\r', '\n', '\t'); }
            catch (DecoderFallbackException) { return entry; }
            if (!text.EndsWith(".img", StringComparison.OrdinalIgnoreCase) && !text.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)) return entry;
            if (depth == 8) throw new InvalidDataException(Strings.SuperReferenceInvalid);
            string relative = FirmwarePath.Normalize(text); int slash = name.LastIndexOf('/'); name = slash < 0 ? relative : name[..(slash + 1)] + relative;
        }
        throw new InvalidDataException(Strings.SuperReferenceInvalid);
    }
    private static InvalidDataException Invalid() => new(Strings.SuperDefinitionInvalid);
}
