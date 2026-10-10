using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal static class CommandLine
{
    public static CliOptions Parse(string[] args) => ParseCore(args, new CliOptions());
    internal static CliOptions ParseSession(string[] args, CliOptions defaults) => ParseCore(args, defaults);
    private static CliOptions ParseCore(string[] args, CliOptions defaults)
    {
        var positional = new List<string>();
        var builder = defaults;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help") return builder with { Command = "help",
                Protocol = builder.Protocol ?? (builder.SprdPac is not null ? "sprd" : null) };
            if (arg is "-v" or "--verbose") { builder = builder with { Verbose = true }; continue; }
            if (arg == "--non-interactive") { builder = builder with { NonInteractive = true }; continue; }
            if (arg == "--sprd-pad-odd") { builder = builder with { SprdPadOdd = true }; continue; }
            if (arg == "--sprd-disable-transcode") { builder = builder with { SprdDisableTranscode = true }; continue; }
            if (arg == "--sprd-entry-transcode-disabled") { builder = builder with { SprdEntryTranscodeDisabled = true }; continue; }
            if (arg == "--oplus-resume") { builder = builder with { OplusResume = true }; continue; }
            if (arg == "--mtk-nand-write") { builder = builder with { MtkNandWrite = true }; continue; }
            if (arg == "--mtk-iot") { builder = builder with { MtkIoT = true }; continue; }
            if (arg == "--mtk-brom-zlp") { builder = builder with { MtkBromZeroLengthPacket = true }; continue; }
            string? value = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : null;
            string name = arg.Contains('=') ? arg[..arg.IndexOf('=')] : arg;
            if (name == "--program-write-mode")
            {
                value ??= i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] :
                    throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                builder = builder with { ProgramWriteMode = value.ToLowerInvariant() switch
                {
                    "program" => FirehoseProgramWriteMode.Program,
                    "auto" => FirehoseProgramWriteMode.Auto,
                    "patch" => FirehoseProgramWriteMode.Patch,
                    _ => throw new ArgumentException(Strings.Cli_ProgramWriteModeInvalid)
                }, HasExplicitProgramWriteMode = true };
                continue;
            }
            if (MtkCommandRequest.LocalOptions.Contains(name) && positional.FirstOrDefault() is { } root &&
                (root.Equals("mtk", StringComparison.OrdinalIgnoreCase) || root.StartsWith("mtk-", StringComparison.OrdinalIgnoreCase)))
            {
                value ??= i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                positional.Add(name + "=" + value);
                continue;
            }
            if (name == "--mtk-brom-chunk")
            {
                value ??= i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] :
                    throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                builder = builder with { MtkBromChunkSize = checked((int)CommandSyntax.Number(value)) };
                continue;
            }
            if (name is "--pac" or "--sprd-fdl2" or "--sprd-fdl1-address" or "--sprd-fdl2-address" or "--sprd-entry" or "--sprd-partition-unit" or "--sprd-length" or
                "--sprd-partition-source" or "--sprd-sector-size" or "--sprd-gpt-bytes" or "--sprd-raw-mode" or "--sprd-raw-flush" or "--sprd-raw-usb-packet" or "--sprd-block-size")
            {
                value ??= i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] :
                    throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                builder = name switch
                {
                    "--pac" => builder with { SprdPac = value, SprdPacPrepared = false },
                    "--sprd-block-size" => builder with { SprdBlockSize = ParseSprdBlockSize(value) },
                    "--sprd-fdl2" => builder with { SprdFdl2 = value, SprdPacPrepared = false },
                    "--sprd-fdl1-address" => builder with { SprdFdl1Address = checked((uint)CommandSyntax.Number(value)), SprdPacPrepared = false },
                    "--sprd-fdl2-address" => builder with { SprdFdl2Address = checked((uint)CommandSyntax.Number(value)), SprdPacPrepared = false },
                    "--sprd-partition-unit" => builder with { SprdPartitionUnit = checked((long)CommandSyntax.Number(value)) },
                    "--sprd-sector-size" => builder with { SprdSectorSize = value.Equals("auto", StringComparison.OrdinalIgnoreCase) ? 0 : ParseSprdSectorSize(value) },
                    "--sprd-gpt-bytes" => builder with { SprdGptBytes = checked((int)CommandSyntax.Number(value)) },
                    "--sprd-raw-flush" => builder with { SprdRawFlush = checked((int)CommandSyntax.Number(value)) },
                    "--sprd-raw-usb-packet" => builder with { SprdRawUsbPacket = checked((int)CommandSyntax.Number(value)) },
                    "--sprd-partition-source" => builder with { SprdPartitionSource = value.ToLowerInvariant() switch
                    { "auto" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdPartitionTableSource.Auto,
                      "native" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdPartitionTableSource.Native,
                      "gpt" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdPartitionTableSource.UserPartitionGpt,
                      _ => throw new ArgumentException(Strings.Cli_SprdProfileInvalid) } },
                    "--sprd-raw-mode" => builder with { SprdRawMode = value.ToLowerInvariant() switch
                    { "off" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdRawDataMode.Disabled,
                      "v1" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdRawDataMode.Version1,
                      "v2" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdRawDataMode.Version2,
                      _ => throw new ArgumentException(Strings.Cli_SprdProfileInvalid) } },
                    "--sprd-entry" => builder with { SprdEntry = value.ToLowerInvariant() switch
                    { "auto" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdBootStage.Auto,
                      "brom" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdBootStage.BootRom,
                      "fdl1" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdBootStage.Fdl1,
                      "fdl2" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdBootStage.Fdl2,
                      _ => throw new ArgumentException(Strings.Cli_SprdProfileInvalid) } },
                    _ => builder with { SprdLengthEncoding = value.ToLowerInvariant() switch
                    { "32" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdPartitionLengthEncoding.UInt32,
                      "64" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdPartitionLengthEncoding.UInt64,
                      "64-reserved" => GeekFlashCore.Protocol.Sprd.Abstractions.SprdPartitionLengthEncoding.UInt64WithReserved,
                      _ => throw new ArgumentException(Strings.Cli_SprdProfileInvalid) } }
                };
                continue;
            }
            if(name=="--mtk-extension-abi")
            {
                value??=i+1<args.Length?args[++i]:throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                builder=builder with {HasExplicitMtkExtensionAbi=true, MtkExtensionAbi=value.ToLowerInvariant() switch
                {"legacy" or "1"=>GeekFlashCore.Protocol.Mtk.Abstractions.MtkExtensionAbi.Legacy,"penumbra2" or "2"=>GeekFlashCore.Protocol.Mtk.Abstractions.MtkExtensionAbi.Penumbra2,_=>throw new ArgumentException(Strings.Cli_MtkExtensionAbiInvalid)}};
                continue;
            }
            if (name == "--mtk-operation-timeout")
            {
                value ??= i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                builder = builder with { MtkOperationTimeout = checked((int)CommandSyntax.Number(value)) };
                continue;
            }
            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                if (name is not ("--port" or "--usb" or "--protocol" or "--loader" or "--digest" or "--vip-signed" or "--vip-chained" or "--oplus-digest" or "--oplus-sign" or "--oplus-mode" or "--oneplus-projid" or "--vendor" or "--auth" or "--read-timeout" or "--write-timeout" or "--connect-timeout" or "--resource-timeout" or "--device-wait-timeout" or "--log-file" or "--mtk-preloader" or "--mtk-da-mode" or "--mtk-auth-file" or "--mtk-cert" or "--usb-serial" or "--usb-bus" or "--usb-port-path" or "--mtk-sej-base" or "--mtk-tzcc-base" or "--mtk-ssr-base" or "--usb-interface" or "--usb-control-interface" or "--usb-alt" or "--mtk-ufs-rpmb-blocks" or "--mtk-nor-erase-block" or "--mtk-pmt-layout" or "--mtk-nand-capacity"))
                    throw new ArgumentException(Strings.FormatCli_UnknownOption(name));
                value ??= i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                builder = name switch
                {
                    "--port" => builder with { Port = value }, "--usb" => builder with { Usb = value }, "--protocol" => builder with { Protocol = value },
                    "--loader" => builder with { Loader = value, SprdPacPrepared = false }, "--digest" => builder with { Digest = value }, "--vip-signed" => builder with { VipSigned = value },
                    "--mtk-preloader" => builder with { MtkPreloader = value },
                    "--mtk-da-mode" => builder with { MtkDaMode = value },
                    "--mtk-auth-file" => builder with { MtkAuthenticationFile = value },
                    "--mtk-cert" => builder with { MtkCertificateFile = value },
                    "--mtk-nor-erase-block" => builder with { MtkNorEraseBlockSize = checked((int)CommandSyntax.Number(value)) },
                    "--mtk-pmt-layout" => builder with { MtkPmtLayout = value },
                    "--mtk-nand-capacity" => builder with { MtkNandCapacity = checked((long)CommandSyntax.Number(value)) },
                    "--usb-serial" => builder with { UsbSerial = value },
                    "--usb-bus" => builder with { UsbBus = byte.Parse(value) },
                    "--usb-port-path" => builder with { UsbPortPath = value },
                    "--usb-interface" => builder with { UsbInterface = int.Parse(value) },
                    "--usb-control-interface" => builder with { UsbControlInterface = int.Parse(value) },
                    "--usb-alt" => builder with { UsbAlternateSetting = int.Parse(value) },
                    "--mtk-ufs-rpmb-blocks" => builder with { MtkUfsRpmbBlocks = value.Split(',').Select(v=>checked((uint)CommandSyntax.Number(v))).ToArray() },
                    "--mtk-sej-base" => builder with { MtkSejBase = checked((uint)CommandSyntax.Number(value)) },
                    "--mtk-tzcc-base" => builder with { MtkTzccBase = checked((uint)CommandSyntax.Number(value)) },
                    "--mtk-ssr-base" => builder with { MtkSsrBase = checked((uint)CommandSyntax.Number(value)) },
                    "--vip-chained" => builder with { VipChained = value }, "--oplus-digest" => builder with { OplusDigest = value },
                    "--oplus-sign" => builder with { OplusSign = value }, "--log-file" => builder with { LogFile = value },
                    "--oplus-mode" => builder with { OplusMode = Enum.Parse<OplusDigestMode>(value, true), HasExplicitOplusMode = true }, "--vendor" => builder with { Vendor = Enum.Parse<QcomVendorKind>(value, true) },
                    "--oneplus-projid" => builder with { OnePlusProjectId = value },
                    "--auth" => builder with { AuthenticationKind = ParseAuthentication(value) },
                    "--read-timeout" => builder with { ReadTimeout = int.Parse(value) }, "--write-timeout" => builder with { WriteTimeout = int.Parse(value) },
                    "--connect-timeout" => builder with { ConnectTimeout = int.Parse(value), HasExplicitConnectTimeout = true },
                    "--resource-timeout" => builder with { ResourceTimeout = int.Parse(value) },
                    "--device-wait-timeout" => builder with { DeviceWaitTimeout = int.Parse(value), HasExplicitDeviceWaitTimeout = true },
                    _ => throw new ArgumentException(Strings.FormatCli_UnknownOption(name))
                };
                continue;
            }
            positional.Add(arg);
        }
        builder = builder with { Command = positional.FirstOrDefault() ?? "interactive", Arguments = positional.Skip(1).ToArray(),
            Protocol = builder.Protocol ?? (builder.SprdPac is not null ? "sprd" : null) };
        builder = MtkCommandRequest.Normalize(builder);
        builder.Validate();
        if (builder.NonInteractive && builder.Command == "interactive")
            throw new ArgumentException(Strings.Cli_NonInteractiveCommandRequired);
        return builder;
    }

    private static int ParseSprdSectorSize(string value)
    {
        int sector = checked((int)CommandSyntax.Number(value));
        if (sector is not (512 or 4096)) throw new ArgumentException(Strings.Cli_SprdProfileInvalid);
        return sector;
    }
    private static int ParseSprdBlockSize(string value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int size) ||
            size is < 1 or > 65534) throw new ArgumentException(Strings.Cli_SprdProfileInvalid);
        return size;
    }
    private static QcomAuthenticationKind ParseAuthentication(string value) =>
        value.Equals("xiaomi", StringComparison.OrdinalIgnoreCase)
            ? QcomAuthenticationKind.XiaomiSignature
            : Enum.Parse<QcomAuthenticationKind>(value, true);

    public static void PrintHelp()
        => PrintRequestedHelp([], new ConsoleUi());

    internal static void PrintRequestedHelp(string[] arguments, ConsoleUi ui)
    {
        string? requested = arguments.FirstOrDefault()?.ToLowerInvariant();
        if (requested == "lp") { LpCommands.PrintHelp(ui); return; }
        if (requested is "sprd" or "unisoc" or "spreadtrum") { PrintSections(ui, Strings.Cli_HelpSprd, Strings.Cli_HelpSprdPac, Strings.Cli_HelpSprdBrowser); return; }
        if (requested == "mtk-scatter") { PrintSections(ui, Strings.Cli_HelpMtkScatterWorkflow, Strings.Cli_HelpMtkRepair); return; }
        if (requested is "mtk" or "mediatek" || requested?.StartsWith("mtk-", StringComparison.OrdinalIgnoreCase) == true) { PrintMtkHelp(ui); return; }
        if (requested == "sprd-chip-uid") { PrintUsage("sprd-chip-uid", ui); return; }
        if (requested is not (null or "all" or "qcom"))
        {
            if (CommandSyntax.Usages.TryGetValue(requested, out string? common)) PrintUsage(common, ui);
            else if (FirehoseCommands.Usages.TryGetValue(requested, out string? firehose)) PrintUsage(firehose, ui);
            else throw new ArgumentException(Strings.FormatCli_UnknownCommand(requested));
            return;
        }
        if (requested is null)
        {
            PrintSections(ui, Strings.Cli_Title + Environment.NewLine + Strings.Cli_HelpUsage,
                Strings.Cli_HelpOverview, Strings.Cli_HelpDetailsHint);
            return;
        }
        if (requested == "all")
        {
            PrintSections(ui, Strings.Cli_Title + Environment.NewLine + Strings.Cli_HelpUsage,
                Strings.Cli_HelpCommands);
            foreach (var command in CommandSyntax.Usages.Where(x => x.Value.Length > 0)) PrintUsage(command.Value, ui);
            PrintSections(ui, "", Strings.Cli_HelpOptionsPrimary, Strings.Cli_HelpOptionsTimeouts);
        }
        if (requested == "all") ui.WriteLine("");
        ui.WriteHelp(Strings.Cli_HelpQcomCommands);
        foreach (string usage in FirehoseCommands.Usages.Values) PrintUsage(usage, ui);
        PrintSections(ui, "", Strings.Cli_HelpOptionsSecondary, Strings.Cli_HelpVipOptions, Strings.Cli_HelpOptionsLegacy);
        if (requested == "qcom")
        {
            PrintSections(ui, "", Strings.Cli_HelpOptionsPrimary, Strings.Cli_HelpOptionsTimeouts);
            return;
        }
        ui.WriteLine("");
        PrintMtkHelp(ui);
        PrintSections(ui, "", Strings.Cli_HelpSprd, Strings.Cli_HelpSprdPac, Strings.Cli_HelpSprdBrowser);
    }

    private static void PrintMtkHelp(ConsoleUi ui) => PrintSections(ui, Strings.Cli_HelpMtk,
        Strings.Cli_HelpMtkCommands, Strings.Cli_HelpMtkScatterWorkflow, Strings.Cli_HelpMtkRepair);

    internal static void PrintSections(ConsoleUi ui, params string[] sections)
    {
        for (int index = 0; index < sections.Length; index++)
        {
            if (index > 0 && sections[index - 1].Length > 0) ui.WriteLine("");
            ui.WriteHelp(sections[index]);
        }
    }

    internal static void PrintUsage(string usage, ConsoleUi ui)
    {
        foreach (string form in usage.Split(" | ", StringSplitOptions.RemoveEmptyEntries)) ui.WriteHelp("  " + form);
    }
}
