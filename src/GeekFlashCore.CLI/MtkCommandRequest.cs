using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed record MtkCommandRequest(string[] Arguments, IReadOnlyDictionary<string, string> Options)
{
    internal static readonly string[] LocalOptions = ["--region", "--start", "--count", "--key-file", "--backup", "--maximum", "--partition", "--partitions", "--verify"];
    internal static readonly string[] Commands = ["probe", "capabilities", "seccfg", "slot", "rpmb", "rpmb-lock", "key", "memory", "register", "partition", "scatter", "efuse", "query", "property", "pmt", "fill", "rsc"];
    internal static CliOptions Normalize(CliOptions options)
    {
        if (!options.Command.Equals("mtk", StringComparison.OrdinalIgnoreCase)) return options;
        if (options.Protocol is not null && (!ProtocolRegistry.TryResolve(options.Protocol, out var registration) ||
            registration.Type != GeekFlashCore.Protocol.Abstractions.ProtocolType.Mtk))
            throw new ArgumentException(Localization.Strings.Cli_MtkOptionConflict);
        if (options.Arguments.Length == 0) return options with { Command = "help", Arguments = ["mtk"] };
        string name = options.Arguments[0].ToLowerInvariant();
        if (!Commands.Contains(name)) throw new CommandUsageException("mtk <" + string.Join('|', Commands) + ">");
        return options with { Protocol = options.Protocol ?? "mtk", Command = "mtk-" + name, Arguments = options.Arguments[1..] };
    }
    internal static MtkCommandRequest Parse(string[] arguments)
    {
        var values = new List<string>(); var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < arguments.Length; i++)
        {
            string item = arguments[i];
            if (!item.StartsWith("--", StringComparison.Ordinal)) { values.Add(item); continue; }
            int separator = item.IndexOf('='); string key = separator < 0 ? item : item[..separator];
            string value = separator >= 0 ? item[(separator + 1)..] : ++i < arguments.Length ? arguments[i] : "";
            if (!LocalOptions.Contains(key) || string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(key, value))
                throw new CommandUsageException("mtk: " + key);
        }
        return new(values.ToArray(), options);
    }
    internal string? Value(string key) => Options.GetValueOrDefault(key);
    internal uint Number(string key, uint fallback = 0) => Value(key) is { } value ? checked((uint)CommandSyntax.Number(value)) : fallback;
    internal void Allow(params string[] keys)
    {
        if (Options.Keys.Any(key => !keys.Contains(key))) throw new CommandUsageException("mtk: " + string.Join(' ', Options.Keys));
    }
    internal static bool TryValidate(string command, string[] args)
    {
        var request = Parse(args); var a = request.Arguments;
        if (command == "mtk-scatter" && a.FirstOrDefault() is not ("to-gpt" or "from-gpt"))
        {
            MtkScatterCommands.ValidateOnline(request);
            return true;
        }
        bool legacy = request.Options.Count == 0 && command switch
        {
            "mtk-seccfg" => a.Length == 5,
            "mtk-slot" => a.Length is 4 or 6,
            "mtk-rpmb" => a.Length is 5 or 6,
            "mtk-rpmb-lock" => a.Length is 2 or 3,
            "mtk-key" => a.FirstOrDefault() is "id" or "input",
            "mtk-partition" => a.Length is 2 or 4,
            "mtk-efuse" => a.Length == 3 || a.Length == 2 && a[0] == "read",
            "mtk-scatter" => a.Length is 2 or 4 || a.FirstOrDefault() is "to-gpt" or "from-gpt",
            _ => true
        };
        if (legacy) return false;
        try
        {
            bool valid = command switch
            {
                "mtk-seccfg" => a.Length == 1 && a[0] is "lock" or "unlock",
                "mtk-slot" => a.Length == 1 && a[0] is "get" or "read" || a.Length == 2 && a[0] == "set" && a[1] is "a" or "b" or "0" or "1",
                "mtk-rpmb" => a.Length == 1 && a[0] is "info" or "erase" || a.Length == 2 && a[0] is "auth" or "read" or "write",
                "mtk-rpmb-lock" => a.Length == 1 && a[0] is "read" or "lock" or "unlock",
                "mtk-key" => a.Length == 2 && Enum.TryParse<MtkKeyDeriveId>(a[0], true, out var id) && Enum.IsDefined(id),
                "mtk-partition" => a.Length == 3 && a[0] is "read" or "write",
                "mtk-efuse" => a.Length == 2 && a[0] is "read" or "write",
                "mtk-scatter" => a.Length == 3 && a[0] is "flash" or "update",
                _ => false
            };
            if (!valid) throw new FormatException();
            switch (command)
            {
                case "mtk-seccfg": request.Allow("--backup", "--partition"); break;
                case "mtk-slot": request.Allow(a[0] == "set" ? ["--backup", "--partition"] : ["--partition"]); break;
                case "mtk-rpmb":
                    request.Allow(a[0] is "read" or "auth" or "info" ? ["--region", "--start", "--count", "--key-file"] : ["--region", "--start", "--count", "--key-file", "--backup"]);
                    if (request.Number("--region") > 3 || request.Value("--count") is not null && request.Number("--count") == 0) throw new FormatException();
                    _ = request.Number("--start");
                    if (a[0] is "auth" or "info") request.Allow("--region");
                    break;
                case "mtk-rpmb-lock": request.Allow(a[0] == "read" ? ["--key-file"] : ["--key-file", "--backup"]); break;
                case "mtk-partition": request.Allow("--maximum"); if (request.Value("--maximum") is { } maximum && CommandSyntax.Number(maximum) is 0 or > long.MaxValue) throw new FormatException(); break;
                case "mtk-efuse": request.Allow(a[0] == "read" ? [] : ["--backup"]); break;
                case "mtk-scatter": request.Allow("--backup"); break;
                default: request.Allow(); break;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new CommandUsageException(command + " (help mtk)"); }
        return true;
    }
}
