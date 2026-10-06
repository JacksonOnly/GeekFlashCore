using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.CLI;

internal static class QcomScriptCommands
{
    internal static bool IsXml(string value) => value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    internal static string? DirectCommand(string value)
    {
        string name = Path.GetFileName(value);
        return !IsXml(name) ? null : name.StartsWith("rawprogram", StringComparison.OrdinalIgnoreCase) ? "rawprogram" :
            name.StartsWith("patch", StringComparison.OrdinalIgnoreCase) ? "patch" : null;
    }

    internal static IReadOnlyList<string> ExpandFiles(string[] patterns)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string pattern in patterns)
        {
            string full = Path.GetFullPath(ConsolePath.Normalize(pattern)!);
            string directory = Path.GetDirectoryName(full)!, name = Path.GetFileName(full);
            if (!Directory.Exists(directory)) throw new FileNotFoundException(Strings.Cli_ScriptFileMissing, pattern);
            IEnumerable<string> matches = name.IndexOfAny(['*', '?']) < 0 ? new[] {full} :
                Directory.EnumerateFiles(directory, name, SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase);
            bool found = false;
            foreach (string file in matches)
            {
                if (!IsXml(file) || !File.Exists(file)) throw new FileNotFoundException(Strings.Cli_ScriptFileMissing, file);
                found = true;
                if (seen.Add(file)) files.Add(file);
                if (files.Count > 1024) throw new ArgumentException(Strings.Cli_ScriptTooManyFiles);
            }
            if (!found) throw new FileNotFoundException(Strings.Cli_ScriptFileMissing, pattern);
        }
        return files;
    }

    internal static void Execute(IQcomProtocol protocol, string[] patterns, bool patchOnly, ConsoleUi ui,
        IProgress<ProgressRecord> progress, CancellationToken ct, TimeProvider? timeProvider = null)
    {
        // Expand all patterns before sending; preserve explicit pattern order and remove duplicates.
        var files = ExpandFiles(patterns);
        var clock = timeProvider ?? TimeProvider.System;
        long batchStarted = clock.GetTimestamp();
        decimal bytes = 0; int executed = 0, skipped = 0;
        foreach (string path in files)
        {
            ct.ThrowIfCancellationRequested();
            ui.WriteLine(Strings.FormatCli_ScriptStarted(Path.GetFileName(path)));
            string directory = Path.GetDirectoryName(path)!;
            long started = clock.GetTimestamp();
            FirehoseScriptResult result = patchOnly
                ? protocol.ExecutePatchFile(new FileDataSource(path), progress, ct)
                : protocol.ExecuteRawProgram(new FileDataSource(path), name => ResolveImage(directory, name), progress, ct);
            TimeSpan elapsed = clock.GetElapsedTime(started);
            PrintStatistics(ui, Strings.FormatCli_ScriptFileSummary(ProgressDisplay.SingleLine(Path.GetFileName(path))),
                result.ExecutedCommands, result.SkippedEntries.Count, result.BytesWritten, elapsed);
            foreach (var entry in result.SkippedEntries) PrintSkipped(ui, entry);
            bytes += result.BytesWritten; executed += result.ExecutedCommands; skipped += result.SkippedEntries.Count;
        }
        PrintStatistics(ui, Strings.FormatCli_ScriptBatchSummary(files.Count), executed, skipped, bytes,
            clock.GetElapsedTime(batchStarted));
    }

    private static void PrintStatistics(ConsoleUi ui, string title, int executed, int skipped, decimal bytes, TimeSpan elapsed)
    {
        double seconds = Math.Max(0, elapsed.TotalSeconds);
        ui.WriteLine(title);
        ui.WriteLine(Strings.FormatCli_ScriptStatistics(executed, skipped, ConsoleUi.FormatBytes(bytes), ProgressDisplay.Duration(seconds)));
        if (bytes > 0) ui.WriteLine("  " + Strings.FormatCli_ProgressSpeed(ProgressDisplay.Rate(bytes, seconds)));
    }

    private static void PrintSkipped(ConsoleUi ui, FirehoseScriptSkippedEntry entry)
    {
        string reason = entry.Reason switch
        {
            FirehoseScriptSkipReason.UnsupportedCommand => Strings.Cli_ScriptSkipUnsupported,
            FirehoseScriptSkipReason.DeviceCommandUnavailable => Strings.Cli_ScriptSkipDevice,
            FirehoseScriptSkipReason.EmptyFileName => Strings.Cli_ScriptSkipEmpty,
            _ => Strings.Cli_ScriptSkipNonDisk
        };
        string name = string.IsNullOrWhiteSpace(entry.Label) ? Strings.Cli_ScriptUnnamedPartition : entry.Label;
        ui.WriteLine(Strings.FormatCli_ScriptSkippedEntry(entry.Index, ProgressDisplay.SingleLine(entry.Command), ProgressDisplay.SingleLine(name)));
        if (entry.PhysicalPartitionNumber is not null || entry.PhysicalPartitionExpression is not null || entry.StartSectorExpression is not null)
            ui.WriteLine(Strings.FormatCli_ScriptSkippedLocation(
                Location(entry.PhysicalPartitionNumber, entry.PhysicalPartitionExpression),
                Location(entry.StartSector, entry.StartSectorExpression), Location(entry.SectorCount, entry.SectorCountExpression)));
        if (!string.IsNullOrWhiteSpace(entry.FileName))
            ui.WriteLine(Strings.FormatCli_ScriptSkippedFile(ProgressDisplay.SingleLine(entry.FileName)));
        ui.WriteLine(Strings.FormatCli_ScriptSkippedReason(reason));
    }

    private static string Location(long? resolved, string? expression)
    {
        string? number = resolved?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? raw = expression is null ? null : ProgressDisplay.SingleLine(expression);
        if (string.IsNullOrWhiteSpace(raw)) return number ?? Strings.Cli_UnknownValue;
        return number is null || raw.Trim().TrimEnd('.') == number ? number ?? raw
            : Strings.FormatCli_ScriptResolvedLocation(number, raw);
    }

    internal static IDataSource ResolveImage(string directory, string filename)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(filename.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), root);
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException(Strings.Cli_ScriptImageOutsideDirectory);
        if (!File.Exists(full)) throw new FileNotFoundException(Strings.Cli_ScriptImageMissing, full);
        return new FileDataSource(full);
    }
}
