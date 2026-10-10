using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Firmware;
using Serilog;

namespace GeekFlashCore.CLI;

/// <summary>Offline, bounded PAC metadata preparation. No extraction or device I/O.</summary>
internal static class SprdPacConfiguration
{
    private const int MaximumXmlBytes = 2 * 1024 * 1024;
    internal static CliOptions Prepare(CliOptions options, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        path = Path.GetFullPath(ConsolePath.Normalize(path)!);
        try
        {
            using var package = FirmwareUnpacker.Open(path, new() { Format = FirmwareFormat.Pac }, ct);
            var fdl1 = Unique(package.Entries.Where(e => e.ResourceId is "FDL" or "FDL1"));
            var fdl2 = Unique(package.Entries.Where(e => e.ResourceId == "FDL2"));
            // Resolve exact names now too: duplicate catalog paths must fail before opening a port.
            package.GetEntry(fdl1.Name); package.GetEntry(fdl2.Name);
            var configurations = package.Entries.Where(e => e.ResourceId == "XML").ToArray();
            if (configurations.Length == 0)
                configurations = package.Entries.Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (configurations.Length is < 1 or > 16) throw Invalid();
            (uint First, uint Second)? addresses = null;
            foreach (var entry in configurations)
            {
                ct.ThrowIfCancellationRequested(); package.GetEntry(entry.Name);
                if (entry.Length is < 1 or > MaximumXmlBytes) throw Invalid();
                using var stream = entry.OpenStream(ct);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumXmlBytes, IgnoreComments = true });
                var document = XDocument.Load(reader, LoadOptions.None);
                if (document.Root?.Name != "BMAConfig") continue;
                var schemeLists = document.Root.Elements("SchemeList").ToArray();
                if (schemeLists.Length != 1) throw Invalid();
                foreach (var scheme in schemeLists[0].Elements("Scheme"))
                {
                    ct.ThrowIfCancellationRequested();
                    var files = scheme.Elements("File").ToArray();
                    var first = files.Where(e => Role(e) is "FDL" or "FDL1").ToArray();
                    var second = files.Where(e => Role(e) == "FDL2").ToArray();
                    if (first.Length == 0 && second.Length == 0) continue;
                    if (first.Length != 1 || second.Length != 1 || addresses is not null) throw Invalid();
                    addresses = (Address(first[0]), Address(second[0]));
                }
            }
            if (addresses is not { } selected) throw Invalid();
            ValidateLoader(fdl1, selected.First, options); ValidateLoader(fdl2, selected.Second, options);
            var prepared = options with { SprdPac = path, SprdPacPrepared = true,
                Loader = path + "::" + fdl1.Name, SprdFdl2 = path + "::" + fdl2.Name,
                SprdFdl1Address = selected.First, SprdFdl2Address = selected.Second };
            prepared.Validate(); ct.ThrowIfCancellationRequested();
            Log.Information(Strings.Cli_SprdPacPrepared, selected.First, selected.Second);
            return prepared;
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException or FormatException or OverflowException)
        { throw new ArgumentException(Strings.Cli_SprdPacInvalid, exception); }
    }
    private static FirmwareEntry Unique(IEnumerable<FirmwareEntry> entries)
    {
        var matches = entries.Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw Invalid();
    }
    private static string? Role(XElement file)
    {
        var roles = file.Elements("ID").ToArray();
        if (roles.Length != 1 || roles[0].HasElements) throw Invalid();
        return roles[0].Value.Trim();
    }
    private static uint Address(XElement file)
    {
        var blocks = file.Elements("Block").ToArray();
        if (blocks.Length != 1) throw Invalid();
        var values = blocks[0].Elements("Base").ToArray();
        if (values.Length != 1 || values[0].HasElements) throw Invalid();
        string value = values[0].Value.Trim();
        if (value.Length > 16) throw Invalid();
        bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture, out uint address)) throw Invalid();
        return address;
    }
    private static void ValidateLoader(FirmwareEntry entry, uint address, CliOptions options)
    {
        long length = entry.Length;
        if (length is < 1 or > 32 * 1024 * 1024 || options.SprdPadOdd && length % 2 != 0 ||
            (ulong)address + (ulong)length > 0x1_0000_0000UL) throw Invalid();
    }
    private static ArgumentException Invalid() => new(Strings.Cli_SprdPacInvalid);
}
