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
            string? value = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : null;
            string name = arg.Contains('=') ? arg[..arg.IndexOf('=')] : arg;
            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                if (name is not ("--port" or "--usb" or "--protocol" or "--loader" or "--digest" or "--vip-signed" or "--vip-chained" or "--oplus-digest" or "--oplus-mode" or "--oneplus-projid" or "--vendor" or "--auth" or "--read-timeout" or "--write-timeout"))
                    throw new ArgumentException(Strings.FormatCli_UnknownOption(name));
                value ??= i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException(Strings.FormatCli_MissingOptionValue(name));
                builder = name switch
                {
                    "--port" => builder with { Port = value }, "--usb" => builder with { Usb = value }, "--protocol" => builder with { Protocol = value },
                    "--loader" => builder with { Loader = value }, "--digest" => builder with { Digest = value }, "--vip-signed" => builder with { VipSigned = value },
                    "--vip-chained" => builder with { VipChained = value }, "--oplus-digest" => builder with { OplusDigest = value },
                    "--oplus-mode" => builder with { OplusMode = Enum.Parse<OplusDigestMode>(value, true) }, "--vendor" => builder with { Vendor = Enum.Parse<QcomVendorKind>(value, true) },
                    "--oneplus-projid" => builder with { OnePlusProjectId = value },
                    "--auth" => builder with { AuthenticationKind = ParseAuthentication(value) },
                    "--read-timeout" => builder with { ReadTimeout = int.Parse(value) }, "--write-timeout" => builder with { WriteTimeout = int.Parse(value) },
                    _ => throw new ArgumentException(Strings.FormatCli_UnknownOption(name))
                };
                continue;
            }
            positional.Add(arg);
        }
        return builder with { Command = positional.FirstOrDefault() ?? "interactive", Arguments = positional.Skip(1).ToArray() };
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
        Console.WriteLine(Strings.Cli_HelpOptionsPrimary);
        Console.WriteLine(Strings.Cli_HelpOptionsSecondary);
    }
}
