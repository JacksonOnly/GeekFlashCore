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
        IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        // Expand all patterns before sending; preserve explicit pattern order and remove duplicates.
        foreach (string path in ExpandFiles(patterns))
        {
            ct.ThrowIfCancellationRequested();
            ui.WriteLine(Strings.FormatCli_ScriptStarted(Path.GetFileName(path)));
            string directory = Path.GetDirectoryName(path)!;
            FirehoseScriptResult result = patchOnly
                ? protocol.ExecutePatchFile(new FileDataSource(path), progress, ct)
                : protocol.ExecuteRawProgram(new FileDataSource(path), name => ResolveImage(directory, name), progress, ct);
            ui.WriteLine(Strings.FormatCli_ScriptCompleted(result.ExecutedCommands, result.SkippedEntries.Count,
                ConsoleUi.FormatBytes(result.BytesWritten)));
            foreach (var group in result.SkippedEntries.GroupBy(x => x.Reason))
            {
                string reason = group.Key switch
                {
                    FirehoseScriptSkipReason.UnsupportedCommand => Strings.Cli_ScriptSkipUnsupported,
                    FirehoseScriptSkipReason.DeviceCommandUnavailable => Strings.Cli_ScriptSkipDevice,
                    FirehoseScriptSkipReason.EmptyFileName => Strings.Cli_ScriptSkipEmpty,
                    _ => Strings.Cli_ScriptSkipNonDisk
                };
                string names = string.Join(", ", group.Select(x => x.Command).Distinct().Take(8));
                ui.WriteLine(Strings.FormatCli_ScriptSkipped(group.Count(), reason, names));
            }
        }
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
