using System.Globalization;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal sealed class CommandUsageException(string usage) : ArgumentException(Strings.FormatCli_Usage(usage));

internal static class CommandSyntax
{
    internal static readonly IReadOnlyDictionary<string, string> Usages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["partitions"] = "partitions <all|lun>",
        ["browse"] = "browse <partition> [lun] [lp-slot]",
        ["browse-image"] = "browse-image <raw-image> [lp-slot]",
        ["firmware"] = FirmwareCommands.Usage,
        ["ls"] = "ls <partition/path> [lun] [lp-slot]",
        ["lp"] = "lp <operation> <container> [...] [lun] [lp-slot] | lp help",
        ["read"] = "read <partition/path> <file> [lun] [lp-slot] | read sector <lun> <start> <count> <file>",
        ["write"] = "write <partition/path> <file> [lun] [lp-slot] | write sector <lun> <start> <count> <file>",
        ["erase"] = "erase <partition> [lun] | erase sector <lun> <start> <count>",
        ["reboot"] = "reboot <system|download|poweroff>",
        ["info"] = "info", ["connect"] = "connect", ["devices"] = "devices",
        ["help"] = "help [command|qcom|all]", ["interactive"] = ""
    };

    public static CliOptions Normalize(CliOptions options)
    {
        string command = options.Command.ToLowerInvariant();
        string[] args = options.Arguments;
        if (command == "parittions") command = "partitions";
        return options with { Command = command, Arguments = args };
    }

    public static void Validate(CliOptions options)
    {
        string command = options.Command;
        string[] a = options.Arguments;
        string usage = Usages.TryGetValue(command, out string? text) ? text : command + " (help)";
        try
        {
            switch (command)
            {
                case "firmware": FirmwareCommands.Validate(a); break;
                case "browse": case "ls":
                    if (a.Length is < 1 or > 3) throw new FormatException();
                    if (a.Length > 1) Lun(a[1]);
                    if (a.Length > 2 && Number(a[2]) > 25) throw new FormatException();
                    break;
                case "lp": LpCommands.Validate(a); break;
                case "browse-image":
                    if (a.Length is < 1 or > 2) throw new FormatException();
                    if (a.Length > 1 && Number(a[1]) > 25) throw new FormatException();
                    break;
                case "read": case "write": case "erase":
                    bool erase = command == "erase";
                    if (a.Length > 0 && a[0].Equals("sector", StringComparison.OrdinalIgnoreCase))
                    {
                        Count(a, erase ? 4 : 5); Lun(a[1]); Range(a[2], a[3]);
                    }
                    else
                    {
                        int mandatory = erase ? 1 : 2;
                        bool nested = !erase && a.Length > 0 && a[0].Contains('/');
                        if (a.Length < mandatory || a.Length > mandatory + (nested ? 2 : 1)) throw new FormatException();
                        if (a.Length > mandatory) Lun(a[mandatory]);
                        if (a.Length > mandatory + 1 && Number(a[mandatory + 1]) > 25) throw new FormatException();
                    }
                    break;
                case "partitions": Count(a, 1); if (!a[0].Equals("all", StringComparison.OrdinalIgnoreCase)) Lun(a[0]); break;
                case "reboot": Count(a, 1); Choice(a[0], "system", "download", "poweroff"); break;
                case "help": if (a.Length > 1) throw new FormatException(); break;
                case "info": case "connect": case "devices": case "interactive": Count(a, 0); break;
                default: throw new FormatException();
            }
            if (a.Any(string.IsNullOrWhiteSpace)) throw new FormatException();
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException)
        { throw new CommandUsageException(usage); }
    }

    public static uint Lun(string value)
    {
        ulong number = Number(value);
        if (number > uint.MaxValue) throw new FormatException();
        return (uint)number;
    }
    public static ulong Number(string value)
    {
        ulong number = Unsigned(value);
        if (number > long.MaxValue) throw new FormatException();
        return number;
    }
    public static ulong Unsigned(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        : ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    internal static void Count(string[] args, int count) { if (args.Length != count) throw new FormatException(); }
    internal static void Choice(string value, params string[] choices) { if (!choices.Contains(value, StringComparer.OrdinalIgnoreCase)) throw new FormatException(); }
    internal static void Range(string start, string count)
    {
        ulong first = Number(start), length = Number(count);
        if (length == 0 || first > (ulong)long.MaxValue - length) throw new FormatException();
    }
}
