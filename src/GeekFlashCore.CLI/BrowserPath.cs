using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal static class BrowserPath
{
    internal static void ValidateNodeName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Any(char.IsControl))
            throw new ArgumentException(Strings.Cli_BrowserInvalidPath);
    }

    internal static string Normalize(string current, string input)
    {
        if (input.Any(char.IsControl)) throw new ArgumentException(Strings.Cli_BrowserInvalidPath);
        var parts = new List<string>();
        foreach (string part in (input.StartsWith('/') ? input : current + "/" + input).Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
            if (parts.Count > 128) throw new ArgumentException(Strings.Cli_BrowserLimit);
        }
        return "/" + string.Join('/', parts);
    }

    internal static void ValidateExportName(string name)
    {
        // Apply portable Windows rules even when the CLI is hosted on Unix.
        string stem = name.Split('.')[0];
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) ||
            new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException(Strings.Cli_BrowserInvalidPath);
    }

    internal static string ExportDestination(string root, string relative)
    {
        string directory = Path.GetFullPath(root);
        string[] parts = relative.Split('/');
        foreach (string part in parts) ValidateExportName(part);
        string destination = Path.GetFullPath(Path.Combine([directory, .. parts]));
        if (!destination.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException(Strings.Cli_BrowserInvalidPath);
        return destination;
    }
}
