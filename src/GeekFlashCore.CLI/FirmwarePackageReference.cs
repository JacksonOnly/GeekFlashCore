using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Firmware;

namespace GeekFlashCore.CLI;

internal sealed class FirmwarePackageReference : IDisposable
{
    private readonly List<FirmwarePackage> _parents = [];
    internal FirmwarePackage Package => _parents[^1];
    internal static IEqualityComparer<string> Comparer { get; } = new ReferenceComparer();
    internal static string Relative(string value, bool wildcard = false)
    {
        value = value.Replace('\\', '/');
        if (value.Length is 0 or > 4096 || value.Contains(':') || value.Any(char.IsControl) ||
            value.Split('/').Any(p => p is "" or "." or "..") || !wildcard && value.IndexOfAny(['*', '?']) >= 0)
            throw new ArgumentException(Strings.Cli_ScriptImageOutsideDirectory);
        return value;
    }
    internal static string Normalize(string value)
    {
        string[] parts = value.Split("::", StringSplitOptions.None);
        if (parts.Length > 8 || string.IsNullOrWhiteSpace(parts[0])) throw new ArgumentException(Strings.Cli_FirmwareScriptSyntax);
        parts[0] = Path.GetFullPath(ConsolePath.Normalize(parts[0])!);
        for (int i = 1; i < parts.Length; i++) parts[i] = Relative(parts[i]);
        return string.Join("::", parts);
    }
    internal static FirmwarePackageReference Open(string value, CancellationToken ct)
    {
        string[] parts = Normalize(value).Split("::", StringSplitOptions.None); var reference = new FirmwarePackageReference();
        try
        {
            reference._parents.Add(FirmwareUnpacker.Open(parts[0], cancellationToken: ct));
            foreach (string entry in parts.Skip(1))
            { ct.ThrowIfCancellationRequested(); reference._parents.Add(FirmwareUnpacker.Open(reference.Package.GetEntry(entry), cancellationToken: ct)); }
            return reference;
        }
        catch { reference.Dispose(); throw; }
    }
    public void Dispose()
    { for (int i = _parents.Count - 1; i >= 0; i--) _parents[i].Dispose(); _parents.Clear(); }

    private sealed class ReferenceComparer : IEqualityComparer<string>
    {
        private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static (string Path, string Entries) Split(string value)
        { int offset = value.IndexOf("::", StringComparison.Ordinal); return offset < 0 ? (value, "") : (value[..offset], value[offset..]); }
        public bool Equals(string? a, string? b)
        { if (a is null || b is null) return a == b; var left = Split(a); var right = Split(b); return Paths.Equals(left.Path, right.Path) && left.Entries == right.Entries; }
        public int GetHashCode(string value)
        { var parts = Split(value); return HashCode.Combine(Paths.GetHashCode(parts.Path), StringComparer.Ordinal.GetHashCode(parts.Entries)); }
    }
}
