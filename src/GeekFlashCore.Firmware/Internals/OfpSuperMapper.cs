using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GeekFlashCore.Android.Sparse;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal static partial class OfpSuperMapper
{
    [GeneratedRegex(@"^(?<directory>(?:[^/]+/)*)super\.(?<index>[0-9]+)\.[0-9a-fA-F]+\.img$", RegexOptions.CultureInvariant)]
    private static partial Regex PartName();

    internal static bool Ofp(FirmwareCatalog c, XElement root)
    {
        if (c.Entries.Any(e => e.Name == "super.img" && e.Length > 0)) return true;
        var super = FirmwareXml.Section(root, "Super"); if (super is null) return false;
        string[] declared = super.Elements("program").Select(e => (string?)e.Attribute("filename"))
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => FirmwarePath.Normalize(n!)).ToArray();
        if (declared.Length == 0) return false;
        var variants = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var nv in FirmwareXml.Section(root, "NVList")?.Elements("nv") ?? Enumerable.Empty<XElement>())
        {
            c.Check(); string? id = (string?)nv.Attribute("id");
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException(Strings.InvalidMetadata);
            var sequence = new List<(int Index, string Name)>();
            foreach (var a in nv.Attributes().Where(a => a.Name.LocalName.StartsWith("super", StringComparison.Ordinal)))
            {
                if (!int.TryParse(a.Name.LocalName.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0)
                    throw new InvalidDataException(Strings.InvalidMetadata);
                if (!string.IsNullOrWhiteSpace(a.Value)) sequence.Add((index, FirmwarePath.Normalize(a.Value)));
            }
            string[] names = Ordered(sequence); if (!variants.TryAdd(id, names)) throw new InvalidDataException(Strings.InvalidMetadata);
        }
        string[] chosen;
        if (c.Options.OfpSuperNvId is { } selected)
        {
            if (!variants.TryGetValue(selected, out var match)) throw new InvalidDataException(Strings.OfpSuperVariantMissing);
            chosen = match;
        }
        else if (variants.Count > 0)
        {
            chosen = variants.Values.First();
            if (variants.Values.Any(v => !v.SequenceEqual(chosen, StringComparer.Ordinal))) throw new InvalidDataException(Strings.OfpSuperVariantRequired);
        }
        else chosen = Ordered(declared.Select(n => { var match = PartName().Match(n); return (Index(match), n); }).ToList());
        if (chosen.Any(n => !declared.Contains(n, StringComparer.Ordinal))) throw new InvalidDataException(Strings.InvalidMetadata);
        Add(c, "super.img", chosen);
        return true;
    }

    internal static void Directory(FirmwareCatalog c)
    {
        var groups = c.Entries.Select(e => (Entry: e, Match: PartName().Match(e.Name))).Where(e => e.Match.Success)
            .GroupBy(e => e.Match.Groups["directory"].Value, StringComparer.Ordinal).ToArray();
        foreach (var group in groups)
        {
            c.Check(); string name = group.Key + "super.img";
            if (c.Entries.Any(e => e.Name == name)) continue;
            Add(c, name, Ordered(group.Select(e => (Index(e.Match), e.Entry.Name)).ToList()));
        }
    }
    private static int Index(Match match)
    {
        if (!match.Success || !int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            throw new InvalidDataException(Strings.InvalidMetadata);
        return index;
    }
    private static string[] Ordered(List<(int Index, string Name)> sequence)
    {
        sequence.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        if (sequence.Count == 0 || sequence.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != sequence.Count)
            throw new InvalidDataException(Strings.InvalidMetadata);
        for (int i = 0; i < sequence.Count; i++) if (sequence[i].Index != i) throw new InvalidDataException(Strings.InvalidMetadata);
        return sequence.Select(e => e.Name).ToArray();
    }
    private static void Add(FirmwareCatalog c, string name, string[] names)
    {
        var sources = new List<Func<CancellationToken, Stream>>();
        foreach (string part in names)
        {
            var matches = c.Entries.Where(e => e.Name == part).Take(2).ToArray();
            if (matches.Length != 1) throw new InvalidDataException(Strings.InvalidMetadata);
            FirmwareEntry entry = matches[0]; sources.Add(ct => entry.OpenStream(ct));
        }
        SparseImageComposition? image = null;
        var options = new SparseImageCompositionOptions
        { MaximumChunks = c.Options.MaximumSegments, MaximumMetadataBytes = c.Options.MaximumMetadataBytes };
        // Both resolution and opening run under the package gate. Never scan payload just to list names.
        c.Deferred(name, ct => GetImage(ct).EncodedLength, ct => GetImage(ct).OpenStream(ct));
        SparseImageComposition GetImage(CancellationToken ct)
        {
            c.Package.Check(ct);
            if (image is not null) return image;
            try
            {
                var parsed = SparseImageComposer.Compose(sources, options, ct, c.Cancellation);
                c.Package.Check(ct); return image = parsed;
            }
            catch (SparseException e) { throw new InvalidDataException(Strings.InvalidMetadata, e); }
        }
    }
}
