using GeekFlashCore.Firmware;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal static class FirmwareCommands
{
    internal const string Usage = "firmware list <package> | firmware extract <package> <entry> <output>";
    internal static void Validate(string[] args)
    {
        if (args.Length == 2 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase) ||
           args.Length == 4 && args[0].Equals("extract", StringComparison.OrdinalIgnoreCase)) return;
        throw new CommandUsageException(Usage);
    }
    internal static void Execute(string[] args, ConsoleUi ui, CancellationToken ct)
    {
        Validate(args);
        using var package = FirmwareUnpacker.Open(ConsolePath.Normalize(args[1])!, cancellationToken: ct);
        ui.WriteLine(Strings.FormatCli_FirmwareCatalog(package.Format, package.Entries.Count));
        if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var e in package.Entries) { ct.ThrowIfCancellationRequested(); ui.WriteLine($"{e.Index,5}  {ConsoleUi.FormatBytes(e.Length),12}  {e.Name}"); }
            return;
        }
        var entry = package.GetEntry(args[2]);
        // The user names a single output file; archive paths never determine a filesystem destination.
        using var output = new FileStream(ConsolePath.Normalize(args[3])!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        entry.CopyTo(output, cancellationToken: ct); ui.WriteLine(Strings.FormatCli_FirmwareExtracted(entry.Name, ConsoleUi.FormatBytes(entry.Length)));
    }
}
