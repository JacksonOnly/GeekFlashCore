using GeekFlashCore.Protocol.Qcom.Abstractions;

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
                if (name is not ("--port" or "--usb" or "--protocol" or "--loader" or "--digest" or "--vip-signed" or "--vip-chained" or "--oplus-digest" or "--oplus-mode" or "--vendor" or "--auth" or "--read-timeout" or "--write-timeout"))
                    throw new ArgumentException($"未知选项 {name}");
                value ??= i + 1 < args.Length ? args[++i] : throw new ArgumentException($"缺少 {name} 的值");
                builder = name switch
                {
                    "--port" => builder with { Port = value }, "--usb" => builder with { Usb = value }, "--protocol" => builder with { Protocol = value },
                    "--loader" => builder with { Loader = value }, "--digest" => builder with { Digest = value }, "--vip-signed" => builder with { VipSigned = value },
                    "--vip-chained" => builder with { VipChained = value }, "--oplus-digest" => builder with { OplusDigest = value },
                    "--oplus-mode" => builder with { OplusMode = Enum.Parse<OplusDigestMode>(value, true) }, "--vendor" => builder with { Vendor = Enum.Parse<QcomVendorKind>(value, true) },
                    "--auth" => builder with { AuthenticationKind = ParseAuthentication(value) },
                    "--read-timeout" => builder with { ReadTimeout = int.Parse(value) }, "--write-timeout" => builder with { WriteTimeout = int.Parse(value) },
                    _ => throw new ArgumentException($"未知选项 {name}")
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
        Console.WriteLine("GeekFlashCore CLI");
        Console.WriteLine("用法: geekflash [选项] <命令> [参数]");
        Console.WriteLine("命令: devices | connect | info | partitions | read | write | erase | reboot");
        foreach (string usage in CommandSyntax.Usages.Values.Where(x => x.Length > 0)) Console.WriteLine("  " + usage);
        Console.WriteLine("选项: --port COM3 | --usb VID:PID | --protocol QualcommEdl | --loader FILE | --digest FILE | --oplus-digest FILE");
        Console.WriteLine("      --oplus-mode OplusDigestPt|OplusDigestLegacy | --vendor NAME | --auth xiaomi (覆盖内置签名) | --verbose");
    }
}
