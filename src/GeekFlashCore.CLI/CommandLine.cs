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
            if (arg == "--oplus-resume") { builder = builder with { OplusResume = true }; continue; }
            if (arg == "--mtk-nand-write") { builder = builder with { MtkNandWrite = true }; continue; }
            if (arg == "--mtk-iot") { builder = builder with { MtkIoT = true }; continue; }
            string? value = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : null;
            string name = arg.Contains('=') ? arg[..arg.IndexOf('=')] : arg;
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

    private static QcomAuthenticationKind ParseAuthentication(string value) =>
        value.Equals("xiaomi", StringComparison.OrdinalIgnoreCase)
            ? QcomAuthenticationKind.XiaomiSignature
            : Enum.Parse<QcomAuthenticationKind>(value, true);

    public static void PrintHelp()
    {
        Console.WriteLine(Strings.Cli_Title);
        Console.WriteLine(Strings.Cli_HelpUsage);
        Console.WriteLine(Strings.Cli_HelpCommands);
        foreach (string usage in CommandSyntax.Usages.Values.Where(x => x.Length > 0)) Console.WriteLine("  " + usage);
        Console.WriteLine(Strings.Cli_HelpQcomCommands);
        foreach (string usage in FirehoseCommands.Usages.Values) Console.WriteLine("  " + usage);
        Console.WriteLine(Strings.Cli_HelpOptionsPrimary);
        Console.WriteLine(Strings.Cli_HelpOptionsSecondary);
        Console.WriteLine(Strings.Cli_HelpVipOptions);
        Console.WriteLine(Strings.Cli_HelpOptionsTimeouts);
        Console.WriteLine(Strings.Cli_HelpOptionsLegacy);
        Console.WriteLine(Strings.Cli_HelpMtk);
        Console.WriteLine(Strings.Cli_HelpMtkStandard);
        Console.WriteLine(Strings.Cli_HelpMtkParity);
        Console.WriteLine(Strings.Cli_HelpMtkKeys);
        Console.WriteLine(Strings.Cli_HelpMtkWriteExtras);
    }
}
