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
            string? value = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : null;
            string name = arg.Contains('=') ? arg[..arg.IndexOf('=')] : arg;
            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                if (name is not ("--port" or "--usb" or "--protocol" or "--loader" or "--digest" or "--vip-signed" or "--vip-chained" or "--oplus-digest" or "--oplus-sign" or "--oplus-mode" or "--oneplus-projid" or "--vendor" or "--auth" or "--read-timeout" or "--write-timeout" or "--connect-timeout" or "--resource-timeout" or "--device-wait-timeout" or "--log-file"))
                    throw new ArgumentException(Strings.FormatCli_UnknownOption(name));
                value ??= i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                builder = name switch
                {
                    "--port" => builder with { Port = value }, "--usb" => builder with { Usb = value }, "--protocol" => builder with { Protocol = value },
                    "--loader" => builder with { Loader = value }, "--digest" => builder with { Digest = value }, "--vip-signed" => builder with { VipSigned = value },
                    "--vip-chained" => builder with { VipChained = value }, "--oplus-digest" => builder with { OplusDigest = value },
                    "--oplus-sign" => builder with { OplusSign = value }, "--log-file" => builder with { LogFile = value },
                    "--oplus-mode" => builder with { OplusMode = Enum.Parse<OplusDigestMode>(value, true), HasExplicitOplusMode = true }, "--vendor" => builder with { Vendor = Enum.Parse<QcomVendorKind>(value, true) },
                    "--oneplus-projid" => builder with { OnePlusProjectId = value },
                    "--auth" => builder with { AuthenticationKind = ParseAuthentication(value) },
                    "--read-timeout" => builder with { ReadTimeout = int.Parse(value) }, "--write-timeout" => builder with { WriteTimeout = int.Parse(value) },
                    "--connect-timeout" => builder with { ConnectTimeout = int.Parse(value) },
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
    }
}
