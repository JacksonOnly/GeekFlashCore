using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Firmware;
using System.IO.Enumeration;

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

    internal static bool TryPackageScript(string value, out string package, out string entry)
    {
        int delimiter = value.IndexOf("::", StringComparison.Ordinal);
        package = entry = "";
        if (delimiter < 0) return false;
        if (delimiter == 0 || delimiter + 2 == value.Length || value.IndexOf("::", delimiter + 2, StringComparison.Ordinal) >= 0)
            throw new ArgumentException(Strings.Cli_FirmwareScriptSyntax);
        package = Path.GetFullPath(ConsolePath.Normalize(value[..delimiter])!);
        entry = value[(delimiter + 2)..].Replace('\\', '/');
        if (entry.StartsWith('/') || entry.Contains(':') || entry.Any(char.IsControl) ||
            entry.Split('/').Any(s => s is "" or "." or "..")) throw new ArgumentException(Strings.Cli_ScriptImageOutsideDirectory);
        return true;
    }

    internal static IReadOnlyList<string> ExpandFiles(string[] patterns) => ExpandFilesCore(patterns, default);

    private static IReadOnlyList<string> ExpandFilesCore(string[] patterns, CancellationToken ct)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var packageSeen = new HashSet<string>(StringComparer.Ordinal);
        var packages = new Dictionary<string, FirmwarePackage>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            foreach (string pattern in patterns)
            {
                ct.ThrowIfCancellationRequested();
                if (TryPackageScript(pattern, out string packagePath, out string entryPattern))
                {
                    if (!packages.TryGetValue(packagePath, out var package))
                        packages.Add(packagePath, package = FirmwareUnpacker.Open(packagePath, cancellationToken: ct));
                    var entryMatches = package.Entries.Where(e => IsXml(e.Name) && FileSystemName.MatchesSimpleExpression(entryPattern, e.Name, ignoreCase: false))
                        .OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
                    if (entryMatches.Length == 0) throw new FileNotFoundException(Strings.Cli_ScriptFileMissing, pattern);
                    foreach (var entry in entryMatches)
                    {
                        package.GetEntry(entry.Name); // Reject ambiguous paths before any device command.
                        string key = (OperatingSystem.IsWindows() ? packagePath.ToUpperInvariant() : packagePath) + "::" + entry.Name;
                        if (packageSeen.Add(key)) files.Add(packagePath + "::" + entry.Name);
                        if (files.Count > 1024) throw new ArgumentException(Strings.Cli_ScriptTooManyFiles);
                    }
                    continue;
                }
                string full = Path.GetFullPath(ConsolePath.Normalize(pattern)!);
                string directory = Path.GetDirectoryName(full)!, name = Path.GetFileName(full);
                if (!Directory.Exists(directory)) throw new FileNotFoundException(Strings.Cli_ScriptFileMissing, pattern);
                IEnumerable<string> matches = name.IndexOfAny(['*', '?']) < 0 ? new[] { full } :
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
        finally { foreach (var package in packages.Values) package.Dispose(); }
    }

    internal static void Execute(IQcomProtocol protocol, string[] patterns, bool patchOnly, ConsoleUi ui,
        IProgress<ProgressRecord> progress, CancellationToken ct, TimeProvider? timeProvider = null)
    {
        // Expand all patterns before sending; preserve explicit pattern order and remove duplicates.
        var files = ExpandFilesCore(patterns, ct);
        var packages = new Dictionary<string, FirmwarePackage>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            var scripts = new List<(string Name, IDataSource Source, Func<string, IDataSource> Resolver)>();
            // Open all package catalogs and resolve script names before the batch sends anything.
            foreach (string path in files)
            {
                ct.ThrowIfCancellationRequested();
                if (TryPackageScript(path, out string packagePath, out string entryName))
                {
                    if (!packages.TryGetValue(packagePath, out var package))
                        packages.Add(packagePath, package = FirmwareUnpacker.Open(packagePath, cancellationToken: ct));
                    scripts.Add((entryName, package.GetEntry(entryName), name => package.ResolveEntry(entryName, name)));
                }
                else
                {
                    string directory = Path.GetDirectoryName(path)!;
                    scripts.Add((Path.GetFileName(path), new FileDataSource(path), name => ResolveImage(directory, name)));
                }
            }
            var clock = timeProvider ?? TimeProvider.System;
            long batchStarted = clock.GetTimestamp();
            decimal bytes = 0; int executed = 0, skipped = 0;
            foreach (var script in scripts)
            {
                ct.ThrowIfCancellationRequested();
                ui.WriteLine(Strings.FormatCli_ScriptStarted(script.Name));
                long started = clock.GetTimestamp();
                FirehoseScriptResult result = patchOnly
                    ? protocol.ExecutePatchFile(script.Source, progress, ct)
                    : protocol.ExecuteRawProgram(script.Source, script.Resolver, progress, ct);
                TimeSpan elapsed = clock.GetElapsedTime(started);
                PrintStatistics(ui, Strings.FormatCli_ScriptFileSummary(ProgressDisplay.SingleLine(script.Name)),
                    result.ExecutedCommands, result.SkippedEntries.Count, result.BytesWritten, elapsed);
                foreach (var entry in result.SkippedEntries) PrintSkipped(ui, entry);
                bytes += result.BytesWritten; executed += result.ExecutedCommands; skipped += result.SkippedEntries.Count;
            }
            PrintStatistics(ui, Strings.FormatCli_ScriptBatchSummary(files.Count), executed, skipped, bytes,
                clock.GetElapsedTime(batchStarted));
        }
        finally { foreach (var package in packages.Values) package.Dispose(); }
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
