using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using static GeekFlashCore.CLI.CommandSyntax;

namespace GeekFlashCore.CLI;

internal static class FirehoseCommands
{
    internal static readonly IReadOnlyDictionary<string, string> Usages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["program"] = "program <partition> <file> [lun] | program sector <lun> <start> <count> <file> (alias: write)",
        ["rawprogram"] = "rawprogram <xml-or-pattern> [xml-or-pattern ...]",
        ["nop"] = "nop", ["configure"] = "configure", ["power"] = "power <reset|reset_to_edl|off>",
        ["getstorageinfo"] = "getstorageinfo <all|lun>", ["setbootablestoragedrive"] = "setbootablestoragedrive <lun>",
        ["patch"] = "patch <xml-or-pattern> [...] | patch <lun> <start> <byte-offset> <size:1|2|4|8> <value>",
        ["benchmark"] = "benchmark <lun> <read|write|digest> <trials>",
        ["getsha256digest"] = "getsha256digest <lun> <start> <count>",
        ["xblgpt"] = "xblgpt <lun>", ["fixgpt"] = "fixgpt <lun> <grow:0|1>",
        ["peek"] = "peek <address> <byte-count> <file>", ["poke"] = "poke <address> <file>",
        ["firmwarewrite"] = "firmwarewrite <lun> <file>",
        ["xml"] = "qcom xml <file>", ["probe-sahara"] = "qcom probe-sahara"
    };

    public static CliOptions Normalize(CliOptions options)
    {
        if (QcomScriptCommands.DirectCommand(options.Command) is { } direct)
            return options with { Command = direct, Arguments = [options.Command, .. options.Arguments] };
        if (options.Command.Equals("program", StringComparison.OrdinalIgnoreCase) && options.Arguments.Length > 0 &&
            options.Arguments.All(QcomScriptCommands.IsXml)) return options with { Command = "rawprogram" };
        if (!options.Command.Equals("qcom", StringComparison.OrdinalIgnoreCase)) return options;
        if (options.Arguments.Length == 0) throw new CommandUsageException("qcom <command> [arguments] (help)");
        options = options with { Command = options.Arguments[0], Arguments = options.Arguments[1..] };
        if (QcomScriptCommands.DirectCommand(options.Command) is { } nestedDirect)
            return options with { Command = nestedDirect, Arguments = [options.Command, .. options.Arguments] };
        if (options.Command.Equals("program", StringComparison.OrdinalIgnoreCase) && options.Arguments.Length > 0 &&
            options.Arguments.All(QcomScriptCommands.IsXml)) return options with { Command = "rawprogram" };
        return options;
    }

    public static void Validate(CliOptions options)
    {
        string command = options.Command;
        string[] a = options.Arguments;
        string usage = Usages.TryGetValue(command, out string? value) ? value : command + " (help)";
        try
        {
            switch (command)
            {
                case "program": CommandSyntax.Validate(options with { Command = "write" }); break;
                case "getstorageinfo": Count(a, 1); if (!a[0].Equals("all", StringComparison.OrdinalIgnoreCase)) CommandSyntax.Lun(a[0]); break;
                case "setbootablestoragedrive": case "xblgpt": Count(a, 1); CommandSyntax.Lun(a[0]); break;
                case "patch":
                    if (a.Length > 0 && a.All(QcomScriptCommands.IsXml)) break;
                    Count(a, 5); CommandSyntax.Lun(a[0]); Number(a[1]); Number(a[2]);
                    ulong size = Number(a[3]), patchValue = Unsigned(a[4]);
                    if (size is not (1 or 2 or 4 or 8) || size < 8 && patchValue >= (1UL << (int)(size * 8)) || Number(a[2]) > uint.MaxValue)
                        throw new FormatException();
                    break;
                case "benchmark": Count(a, 3); CommandSyntax.Lun(a[0]); Choice(a[1], "read", "write", "digest"); if (Number(a[2]) is 0 or > 1000) throw new FormatException(); break;
                case "getsha256digest": Count(a, 3); CommandSyntax.Lun(a[0]); Range(a[1], a[2]); break;
                case "fixgpt": Count(a, 2); CommandSyntax.Lun(a[0]); Choice(a[1], "0", "1"); break;
                case "peek": Count(a, 3); ulong address = Unsigned(a[0]), length = Number(a[1]); if (length == 0 || length > ulong.MaxValue - address) throw new FormatException(); break;
                case "poke": Count(a, 2); Unsigned(a[0]); break;
                case "firmwarewrite": Count(a, 2); CommandSyntax.Lun(a[0]); break;
                case "xml": Count(a, 1); break;
                case "rawprogram": if (a.Length == 0 || !a.All(QcomScriptCommands.IsXml)) throw new FormatException(); break;
                case "power": Count(a, 1); Choice(a[0], "reset", "reset_to_edl", "off"); break;
                case "nop": case "configure": case "probe-sahara": Count(a, 0); break;
                default: throw new FormatException();
            }
            if (a.Any(string.IsNullOrWhiteSpace)) throw new FormatException();
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException or CommandUsageException)
        { throw new CommandUsageException(usage); }
    }
    private delegate Task Handler(IQcomProtocol protocol, string[] args, ConsoleUi ui, CancellationToken ct);
    private static readonly IReadOnlyDictionary<string, Handler> Handlers = new Dictionary<string, Handler>(StringComparer.OrdinalIgnoreCase)
    {
        ["nop"] = Nop, ["configure"] = Configure, ["getstorageinfo"] = StorageInfo,
        ["patch"] = Patch, ["setbootablestoragedrive"] = SetBootable, ["power"] = Power,
        ["benchmark"] = Benchmark, ["peek"] = Peek, ["poke"] = Poke,
        ["xblgpt"] = XblGpt, ["fixgpt"] = FixGpt, ["getsha256digest"] = Sha256,
        ["firmwarewrite"] = FirmwareWrite
    };

    private static readonly IReadOnlyList<string> ImplementedCommands = Handlers.Keys
        .Concat(["program", "read", "erase"]).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyList<string> ReportedCommands(IQcomProtocol protocol) =>
        protocol.TargetInfo?.Firehose?.BasicDevCharacteristics?.SupportedFunctions
        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    internal static IReadOnlyList<string> MappedCommands(IQcomProtocol protocol) =>
        ReportedCommands(protocol)
        .Select(x => x.ToLowerInvariant())
        .Where(x => Handlers.ContainsKey(x) || x is "program" or "read" or "erase")
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    // Local implementations are candidates for explicit execution, not device capability evidence.
    internal static IReadOnlyList<string> AvailableCommands(IQcomProtocol protocol) =>
        ReportedCommands(protocol).Count == 0 ? ImplementedCommands : MappedCommands(protocol);

    public static void PrintMapping(IProtocol protocol, ConsoleUi ui)
    {
        if (protocol is not IQcomProtocol qcom || !qcom.IsConnected) return;
        var mapped = MappedCommands(qcom);
        if (ReportedCommands(qcom).Count == 0)
        {
            ui.WriteLine(Strings.Cli_CommandListUnknown);
            ui.WriteLine(Strings.Cli_CommandDetailsHint);
            return;
        }
        else ui.WriteLine(Strings.FormatCli_CommandsMapped(mapped.Count));
        if (mapped.Count > 0) ui.WriteLine("  " + string.Join("  |  ", mapped.Select(x => x + " => " + HostCommand(x))));
        if (mapped.Count == 0) ui.WriteLine(Strings.Cli_NoCommandEvidence);
        ui.WriteLine(Strings.Cli_CommandDetailsHint);
    }

    private static string HostCommand(string wire) => wire switch
    { "program" => "write / rawprogram", "read" => "read / partitions / browse", "power" => "reboot", _ => wire };

    internal static void PrintDetails(IQcomProtocol protocol, ConsoleUi ui)
    {
        if (ReportedCommands(protocol).Count == 0) { ui.WriteLine(Strings.Cli_CommandListUnknown); return; }
        foreach (string command in MappedCommands(protocol))
            ui.WriteLine("  " + (Usages.TryGetValue(command, out string? usage) ? usage : CommandSyntax.Usages[command]) + "  " + Description(command));
    }

    private static string Description(string command) => command switch
    {
        "program" => Strings.Cli_ProgramDescription, "read" => Strings.Cli_ReadDescription,
        "erase" => Strings.Cli_EraseDescription, "nop" => Strings.Cli_NopDescription,
        "configure" => Strings.Cli_ConfigureDescription, "power" => Strings.Cli_PowerDescription,
        "getstorageinfo" => Strings.Cli_StorageDescription, "patch" => Strings.Cli_PatchDescription,
        "setbootablestoragedrive" => Strings.Cli_BootableDescription, "benchmark" => Strings.Cli_BenchmarkDescription,
        "peek" => Strings.Cli_PeekDescription, "poke" => Strings.Cli_PokeDescription,
        "getsha256digest" => Strings.Cli_Sha256Description, "firmwarewrite" => Strings.Cli_FirmwareDescription,
        "xblgpt" => Strings.Cli_XblGptDescription, "fixgpt" => Strings.Cli_FixGptDescription,
        _ => throw new InvalidOperationException()
    };

    public static void Require(IQcomProtocol protocol, string command)
    {
        if (!protocol.IsConnected) throw new InvalidOperationException(Strings.Cli_ReconnectRequired);
        string wire = command switch { "write" => "program", "reboot" => "power", _ => command };
        var reported = ReportedCommands(protocol);
        if (!(reported.Count == 0 ? ImplementedCommands : reported).Contains(wire, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException(Strings.FormatCli_CommandNotSupported(wire));
    }

    public static async Task ExecuteAsync(IQcomProtocol protocol, string command, string[] args, ConsoleUi ui, CancellationToken ct)
    {
        if (command != "configure" || protocol.IsConnected) Require(protocol, command);
        ct.ThrowIfCancellationRequested();
        await Handlers[command](protocol, args, ui, ct);
        ui.WriteLine(Strings.FormatCli_CommandCompleted(command));
    }

    private static Task Nop(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    { Check(p.Nop(ct)); return Task.CompletedTask; }
    private static Task Configure(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    { Check(p.ConfigureFirehose()); return Task.CompletedTask; }
    private static Task StorageInfo(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        foreach (uint lun in a[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? p.GetPhysicalPartitions() : [CommandSyntax.Lun(a[0])])
        {
            ct.ThrowIfCancellationRequested();
            var info = p.GetStorageInfo(lun);
            string unknown = Strings.Cli_UnknownValue;
            ui.WriteLine(Strings.FormatCli_StorageInfoLine(
                info.Storage,
                lun,
                info.BlockCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? unknown,
                info.BlockSizeInBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? unknown,
                info.CapacityInBytes is { } size ? ConsoleUi.FormatBytes(size) : unknown));
        }
        return Task.CompletedTask;
    }
    private static Task SetBootable(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    { Check(p.SetBootableStorageDrive(CommandSyntax.Lun(a[0]), ct)); return Task.CompletedTask; }
    private static Task Patch(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        Check(p.Patch(CommandSyntax.Lun(a[0]), checked((long)CommandSyntax.Number(a[1])),
            checked((uint)CommandSyntax.Number(a[2])), checked((uint)CommandSyntax.Number(a[3])),
            CommandSyntax.Unsigned(a[4]), ct));
        return Task.CompletedTask;
    }
    private static async Task Power(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        ProtocolRebootMode mode = a[0].ToLowerInvariant() switch
        { "reset" => ProtocolRebootMode.System, "reset_to_edl" => ProtocolRebootMode.Download, _ => ProtocolRebootMode.PowerOff };
        if (!await p.RebootAsync(mode, ct: ct)) throw new InvalidOperationException(Strings.FormatCli_CommandUnsuccessful("power"));
    }
    private static Task Benchmark(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        FirehoseBenchmarkMode mode = a[1].ToLowerInvariant() switch
        { "read" => FirehoseBenchmarkMode.Read, "write" => FirehoseBenchmarkMode.Write, _ => FirehoseBenchmarkMode.Digest };
        Check(p.Benchmark(CommandSyntax.Lun(a[0]), mode, checked((uint)CommandSyntax.Number(a[2])), ct));
        return Task.CompletedTask;
    }
    private static Task XblGpt(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    { Check(p.XblGpt(CommandSyntax.Lun(a[0]), ct)); return Task.CompletedTask; }
    private static Task FixGpt(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        Check(p.FixGpt(CommandSyntax.Lun(a[0]), a[1] == "1", ct));
        return Task.CompletedTask;
    }
    private static Task Sha256(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        byte[] digest = p.GetSha256Digest(CommandSyntax.Lun(a[0]),
            checked((long)CommandSyntax.Number(a[1])), checked((long)CommandSyntax.Number(a[2])),
            cancellationToken: ct);
        ui.WriteLine("SHA256: " + Convert.ToHexString(digest));
        return Task.CompletedTask;
    }
    private static Task Peek(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        using var stream = File.Create(ConsolePath.Normalize(a[2])!);
        p.Peek(CommandSyntax.Unsigned(a[0]), (long)CommandSyntax.Number(a[1]), stream, ct);
        return Task.CompletedTask;
    }
    private static Task Poke(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    { p.Poke(CommandSyntax.Unsigned(a[0]), new FileDataSource(ConsolePath.Normalize(a[1])!), ct); return Task.CompletedTask; }
    private static Task FirmwareWrite(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    { Check(p.FirmwareWrite(CommandSyntax.Lun(a[0]), new FileDataSource(ConsolePath.Normalize(a[1])!), ct)); return Task.CompletedTask; }

    private static void Check(FirehoseCommandResult result)
    { if (!result.IsSuccess) throw new InvalidOperationException(Strings.FormatCli_CommandUnsuccessful(result.Status)); }
}
