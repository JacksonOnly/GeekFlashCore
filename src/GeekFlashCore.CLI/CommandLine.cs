using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal static class CommandLine
{
    public static CliOptions Parse(string[] args)
    {
        var positional = new List<string>();
        var builder = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help") return builder with { Command = "help" };
            if (arg is "-v" or "--verbose") { builder = builder with { Verbose = true }; continue; }
            if (arg == "--non-interactive") { builder = builder with { NonInteractive = true }; continue; }
            if (arg == "--sprd-pad-odd") { builder = builder with { SprdPadOdd = true }; continue; }
            if (arg == "--sprd-disable-transcode") { builder = builder with { SprdDisableTranscode = true }; continue; }
            if (arg == "--sprd-entry-transcode-disabled") { builder = builder with { SprdEntryTranscodeDisabled = true }; continue; }
            if (arg == "--oplus-resume") { builder = builder with { OplusResume = true }; continue; }
            if (arg == "--mtk-nand-write") { builder = builder with { MtkNandWrite = true }; continue; }
            if (arg == "--mtk-iot") { builder = builder with { MtkIoT = true }; continue; }
            string? value = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : null;
            string name = arg.Contains('=') ? arg[..arg.IndexOf('=')] : arg;
            if (name is "--sprd-fdl2" or "--sprd-fdl1-address" or "--sprd-fdl2-address" or "--sprd-entry" or "--sprd-partition-unit" or "--sprd-length" or
                "--sprd-partition-source" or "--sprd-sector-size" or "--sprd-gpt-bytes" or "--sprd-raw-mode" or "--sprd-raw-flush" or "--sprd-raw-usb-packet")
            {
                value ??= i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] :
                    throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                builder = name switch
                {
                    "--sprd-fdl2" => builder with { SprdFdl2 = value },
                    "--sprd-fdl1-address" => builder with { SprdFdl1Address = checked((uint)CommandSyntax.Number(value)) },
                    "--sprd-fdl2-address" => builder with { SprdFdl2Address = checked((uint)CommandSyntax.Number(value)) },
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
                builder=builder with {MtkExtensionAbi=value.ToLowerInvariant() switch
                {"legacy" or "1"=>GeekFlashCore.Protocol.Mtk.Abstractions.MtkExtensionAbi.Legacy,"penumbra2" or "2"=>GeekFlashCore.Protocol.Mtk.Abstractions.MtkExtensionAbi.Penumbra2,_=>throw new ArgumentException(Strings.Cli_MtkExtensionAbiInvalid)}};
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
                    "--loader" => builder with { Loader = value }, "--digest" => builder with { Digest = value }, "--vip-signed" => builder with { VipSigned = value },
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
                    "--device-wait-timeout" => builder with { DeviceWaitTimeout = int.Parse(value) },
                    _ => throw new ArgumentException(Strings.FormatCli_UnknownOption(name))
                };
                continue;
            }
            positional.Add(arg);
        }
        builder = builder with { Command = positional.FirstOrDefault() ?? "interactive", Arguments = positional.Skip(1).ToArray() };
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
        if (requested is "sprd" or "unisoc" or "spreadtrum") { ui.WriteLine(Strings.Cli_HelpSprd); return; }
        if (requested == "sprd-chip-uid") { PrintUsage("sprd-chip-uid", ui); return; }
        if (requested is not (null or "all" or "qcom"))
        {
            if (CommandSyntax.Usages.TryGetValue(requested, out string? common)) PrintUsage(common, ui);
            else if (FirehoseCommands.Usages.TryGetValue(requested, out string? firehose)) PrintUsage(firehose, ui);
            else throw new ArgumentException(Strings.FormatCli_UnknownCommand(requested));
            return;
        }
        ui.WriteLine(Strings.Cli_Title);
        ui.WriteLine(Strings.Cli_HelpUsage);
        ui.WriteLine(Strings.Cli_HelpCommands);
        foreach (string usage in CommandSyntax.Usages.Values.Where(x => x.Length > 0)) PrintUsage(usage, ui);
        ui.WriteLine("  " + FirehoseCommands.Usages["rawprogram"]);
        ui.WriteLine("  patch <xml-or-pattern> [...]");
        ui.WriteLine(Strings.Cli_InteractiveKeys);
        if (requested is null) { ui.WriteLine(Strings.Cli_HelpDetailsHint); return; }
        ui.WriteLine(Strings.Cli_HelpQcomCommands);
        foreach (string usage in FirehoseCommands.Usages.Values) PrintUsage(usage, ui);
        if (requested == "qcom") return;
        ui.WriteLine(Strings.Cli_HelpOptionsPrimary);
        ui.WriteLine(Strings.Cli_HelpOptionsSecondary);
        ui.WriteLine(Strings.Cli_HelpVipOptions);
        ui.WriteLine(Strings.Cli_HelpOptionsTimeouts);
        ui.WriteLine(Strings.Cli_HelpOptionsLegacy);
        ui.WriteLine(Strings.Cli_HelpMtk);
        ui.WriteLine(Strings.Cli_HelpMtkStandard);
        ui.WriteLine(Strings.Cli_HelpMtkParity);
        ui.WriteLine(Strings.Cli_HelpMtkKeys);
        ui.WriteLine(Strings.Cli_HelpMtkWriteExtras);
        ui.WriteLine(Strings.Cli_HelpSprd);
    }

    internal static void PrintUsage(string usage, ConsoleUi ui)
    {
        foreach (string form in usage.Split(" | ", StringSplitOptions.RemoveEmptyEntries)) ui.WriteLine("  " + form);
    }
}
