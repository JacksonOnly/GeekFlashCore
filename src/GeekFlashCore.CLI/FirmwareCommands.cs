using GeekFlashCore.Firmware;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal static class FirmwareCommands
{
    internal const string Usage = "firmware list <package> | firmware extract <package> <entry> <output> | firmware super-info <package::definition.json>";
    internal static void Validate(string[] args)
    {
        if (args.Length == 2 && (args[0].Equals("list", StringComparison.OrdinalIgnoreCase) || args[0].Equals("super-info", StringComparison.OrdinalIgnoreCase)) ||
           args.Length == 4 && args[0].Equals("extract", StringComparison.OrdinalIgnoreCase)) return;
        throw new CommandUsageException(Usage);
    }
    internal static void Execute(string[] args, ConsoleUi ui, CancellationToken ct)
    {
        Validate(args);
        if (args[0].Equals("super-info", StringComparison.OrdinalIgnoreCase))
        {
            using var input = FirmwarePackageInput.Open(args[1], ct, requireSuper: true);
            var image = input.SuperPlan!; var layout = image.Layout;
            ui.WriteLine(Strings.FormatCli_FirmwareSuperInfo(image.Name, image.LogicalLength,
                layout.Geometry.MetadataMaxSize, layout.Geometry.MetadataSlotCount, layout.Geometry.LogicalBlockSize, layout.Header.Flags));
            foreach (var partition in layout.Partitions.Span)
            {
                ct.ThrowIfCancellationRequested();
                ui.WriteLine(Strings.FormatCli_LpPartitionInfo(partition.Name, partition.RawName, partition.LogicalSize,
                    partition.Attributes, layout.Groups.Span[checked((int)partition.GroupIndex)].Name));
            }
            foreach (var group in layout.Groups.Span)
            { ct.ThrowIfCancellationRequested(); ui.WriteLine(Strings.FormatCli_LpGroupInfo(group.Name, group.RawName, group.MaximumSize, group.Flags)); }
            return;
        }
        using var reference = FirmwarePackageReference.Open(args[1], ct);
        var package = reference.Package;
        ui.WriteLine(Strings.FormatCli_FirmwareCatalog(package.Format, package.Entries.Count));
        if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var e in package.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string size = e.KnownLength is { } length ? ConsoleUi.FormatBytes(length) : Strings.Cli_FirmwareDeferredLength;
                ui.WriteLine($"{e.Index,5}  {size,12}  {e.Name}");
            }
            return;
        }
        var entry = package.GetEntry(args[2]);
        // The user names a single output file; archive paths never determine a filesystem destination.
        using var output = new FileStream(ConsolePath.Normalize(args[3])!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        entry.CopyTo(output, cancellationToken: ct); ui.WriteLine(Strings.FormatCli_FirmwareExtracted(entry.Name, ConsoleUi.FormatBytes(entry.Length)));
    }
}
