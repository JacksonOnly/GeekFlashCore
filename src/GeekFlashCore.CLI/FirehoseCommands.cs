using System.Globalization;
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
        ["nop"] = "nop", ["configure"] = "configure", ["power"] = "power <reset|reset_to_edl|off>",
        ["getstorageinfo"] = "getstorageinfo <all|lun>", ["setbootablestoragedrive"] = "setbootablestoragedrive <lun>",
        ["patch"] = "patch <lun> <start> <byte-offset> <size:1|2|4|8> <value>",
        ["benchmark"] = "benchmark <lun> <read|write|digest> <trials>",
        ["getsha256digest"] = "getsha256digest <lun> <start> <count>",
        ["xblgpt"] = "xblgpt <lun>", ["fixgpt"] = "fixgpt <lun> <grow:0|1>",
        ["peek"] = "peek <address> <byte-count> <file>", ["poke"] = "poke <address> <file>",
        ["firmwarewrite"] = "firmwarewrite <lun> <file>",
        ["xml"] = "qcom xml <file>", ["probe-sahara"] = "qcom probe-sahara"
    };

    public static CliOptions Normalize(CliOptions options)
    {
        if (!options.Command.Equals("qcom", StringComparison.OrdinalIgnoreCase)) return options;
        if (options.Arguments.Length == 0) throw new CommandUsageException("qcom <command> [arguments] (help)");
        return options with { Command = options.Arguments[0], Arguments = options.Arguments[1..] };
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
                    Count(a, 5); CommandSyntax.Lun(a[0]); Number(a[1]); Number(a[2]);
                    ulong size = Number(a[3]), patchValue = Unsigned(a[4]);
                    if (size is not (1 or 2 or 4 or 8) || size < 8 && patchValue >= (1UL << (int)(size * 8))) throw new FormatException();
                    break;
                case "benchmark": Count(a, 3); CommandSyntax.Lun(a[0]); Choice(a[1], "read", "write", "digest"); if (Number(a[2]) is 0 or > 1000) throw new FormatException(); break;
                case "getsha256digest": Count(a, 3); CommandSyntax.Lun(a[0]); Range(a[1], a[2]); break;
                case "fixgpt": Count(a, 2); CommandSyntax.Lun(a[0]); Choice(a[1], "0", "1"); break;
                case "peek": Count(a, 3); ulong address = Unsigned(a[0]), length = Number(a[1]); if (length == 0 || length > ulong.MaxValue - address) throw new FormatException(); break;
                case "poke": Count(a, 2); Unsigned(a[0]); break;
                case "firmwarewrite": Count(a, 2); CommandSyntax.Lun(a[0]); break;
                case "xml": Count(a, 1); break;
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

    internal static IReadOnlyList<string> MappedCommands(IQcomProtocol protocol) =>
        (protocol.TargetInfo?.Firehose?.BasicDevCharacteristics?.SupportedFunctions ?? [])
        .Select(x => x.ToLowerInvariant())
        .Where(x => Handlers.ContainsKey(x) || x is "program" or "read" or "erase")
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static void PrintMapping(IProtocol protocol, ConsoleUi ui)
    {
        if (protocol is not IQcomProtocol qcom || !qcom.IsConnected) return;
        var mapped = MappedCommands(qcom);
        ui.WriteLine(Strings.FormatCli_CommandsMapped(mapped.Count));
        if (mapped.Count > 0) ui.WriteLine(Strings.Cli_FirehoseUsage);
        foreach (string command in mapped)
        {
            ui.WriteLine("  " + (Usages.TryGetValue(command, out string? usage) ? usage : CommandSyntax.Usages[command]));
            ui.WriteLine("    " + Description(command));
        }
        if (mapped.Count == 0) ui.WriteLine(Strings.Cli_NoCommandEvidence);
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
        if (!(protocol.TargetInfo?.Firehose?.BasicDevCharacteristics?.SupportedFunctions ?? []).Contains(wire, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException(Strings.FormatCli_CommandNotSupported(wire));
    }

    public static bool Handles(string command) => Handlers.ContainsKey(command);
    public static async Task ExecuteAsync(IQcomProtocol protocol, string command, string[] args, ConsoleUi ui, CancellationToken ct)
    {
        if (command != "configure" || protocol.IsConnected) Require(protocol, command);
        ct.ThrowIfCancellationRequested();
        await Handlers[command](protocol, args, ui, ct);
        ui.WriteLine(Strings.FormatCli_CommandCompleted(command));
        if (command == "configure") PrintMapping(protocol, ui);
    }

    private static Task Nop(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct) => Send(p, new NopCommand());
    private static Task Configure(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    { Check(p.ConfigureFirehose()); return Task.CompletedTask; }
    private static Task StorageInfo(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        foreach (uint lun in a[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? p.GetPhysicalPartitions() : [Lun(p, a[0])])
        {
            ct.ThrowIfCancellationRequested();
            var info = p.GetStorageInfo(lun);
            ui.WriteLine($"{info.Storage} lun={lun} blocks={info.BlockCount} sector={info.BlockSizeInBytes} capacity={(info.CapacityInBytes is { } size ? ConsoleUi.FormatBytes(size) : "unknown")}");
        }
        return Task.CompletedTask;
    }
    private static Task SetBootable(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct) =>
        Send(p, new SetBootableStorageDriveCommand { Value = Lun(p, a[0]) });
    private static Task Patch(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        uint size = StorageCommands.SectorSize(p);
        ulong offset = CommandSyntax.Number(a[2]), count = CommandSyntax.Number(a[3]);
        if (offset + count > size) throw new ArgumentException(Strings.Cli_RangeOutsideStorage);
        uint lun = Lun(p, a[0]);
        ValidateSectorRange(p, lun, a[1], "1");
        return Send(p, new PatchCommand { PhysicalPartitionNumber = lun, SectorSizeInBytes = size,
            StartSector = Decimal(a[1]), ByteOffset = checked((uint)offset), SizeInBytes = (uint)count,
            Value = CommandSyntax.Unsigned(a[4]).ToString(CultureInfo.InvariantCulture), FileName = "DISK" });
    }
    private static async Task Power(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        ProtocolRebootMode mode = a[0].ToLowerInvariant() switch
        { "reset" => ProtocolRebootMode.System, "reset_to_edl" => ProtocolRebootMode.Download, _ => ProtocolRebootMode.PowerOff };
        if (!await p.RebootAsync(mode, ct: ct)) throw new InvalidOperationException(Strings.FormatCli_CommandUnsuccessful("power"));
    }
    private static Task Benchmark(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct) =>
        Send(p, new BenchmarkCommand { PhysicalPartitionNumber = Lun(p, a[0]), Trials = (uint)CommandSyntax.Number(a[2]),
            TestReadPerformance = a[1].Equals("read", StringComparison.OrdinalIgnoreCase) ? 1u : 0u,
            TestWritePerformance = a[1].Equals("write", StringComparison.OrdinalIgnoreCase) ? 1u : 0u,
            TestDigestPerformance = a[1].Equals("digest", StringComparison.OrdinalIgnoreCase) ? 1u : 0u });
    private static Task XblGpt(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct) =>
        Send(p, new XblGptCommand { Lun = Lun(p, a[0]) });
    private static Task FixGpt(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        uint lun = Lun(p, a[0]);
        return Send(p, new FixGptCommand { PhysicalPartitionNumber = lun, Lun = lun.ToString(CultureInfo.InvariantCulture), GrowLastPartition = byte.Parse(a[1]) });
    }
    private static Task Sha256(IQcomProtocol p, string[] a, ConsoleUi ui, CancellationToken ct)
    {
        uint lun = Lun(p, a[0]);
        ValidateSectorRange(p, lun, a[1], a[2]);
        var result = p.ExecuteFirehoseCommand(new GetSha256DigestCommand { PhysicalPartitionNumber = lun,
            SectorSizeInBytes = StorageCommands.SectorSize(p), StartSector = Decimal(a[1]), NumPartitionSectors = Decimal(a[2]) });
        Check(result);
        foreach (string text in result.Attributes.Values.Concat(result.Logs.Select(x => x.Message)))
        {
            for (int index = 0; index <= text.Length - 64; index++)
            {
                var hex = text.AsSpan(index, 64);
                if (hex.ContainsAnyExcept("0123456789abcdefABCDEF")) continue;
                ui.WriteLine("SHA256: " + hex.ToString());
                return Task.CompletedTask;
            }
        }
        throw new InvalidDataException(Strings.Cli_DigestMissing);
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
    { Check(p.FirmwareWrite(Lun(p, a[0]), new FileDataSource(ConsolePath.Normalize(a[1])!), ct)); return Task.CompletedTask; }

    private static uint Lun(IQcomProtocol p, string value)
    {
        uint lun = CommandSyntax.Lun(value);
        if (!p.GetPhysicalPartitions().Contains(lun)) throw new ArgumentException(Strings.FormatCli_UnknownLun(lun));
        return lun;
    }
    private static void ValidateSectorRange(IQcomProtocol p, uint lun, string start, string count)
    {
        var info = p.TargetInfo?.Firehose?.StorageInfos.FirstOrDefault(x => x.PhysicalPartitionNumber == lun) ?? p.GetStorageInfo(lun);
        if (info.BlockCount is not { } blocks || checked(CommandSyntax.Number(start) + CommandSyntax.Number(count)) > blocks)
            throw new ArgumentException(Strings.Cli_RangeOutsideStorage);
    }
    private static string Decimal(string value) => CommandSyntax.Number(value).ToString(CultureInfo.InvariantCulture);
    private static Task Send(IQcomProtocol p, BaseCommand command) { Check(p.ExecuteFirehoseCommand(command)); return Task.CompletedTask; }
    private static void Check(FirehoseCommandResult result)
    { if (!result.IsSuccess) throw new InvalidOperationException(Strings.FormatCli_CommandUnsuccessful(result.Status)); }
}
