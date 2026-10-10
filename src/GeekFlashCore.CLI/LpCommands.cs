using GeekFlashCore.Android.Lp;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.CLI;

internal static class LpCommands
{
    internal static readonly IReadOnlyDictionary<string, string> Usages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["info"] = "lp info <container>",
        ["rename"] = "lp rename <container> <partition> <new-name>",
        ["resize"] = "lp resize <container> <partition> <bytes>",
        ["attributes"] = "lp attributes <container> <partition> <flags>",
        ["move"] = "lp move <container> <partition> <group>",
        ["add"] = "lp add <container> <name> <group> <bytes> <flags>",
        ["remove"] = "lp remove <container> <partition>",
        ["group-add"] = "lp group-add <container> <name> <maximum-bytes> <flags>",
        ["group-rename"] = "lp group-rename <container> <group> <new-name>",
        ["group-resize"] = "lp group-resize <container> <group> <maximum-bytes>",
        ["group-flags"] = "lp group-flags <container> <group> <flags>",
        ["group-remove"] = "lp group-remove <container> <group>"
    };

    private static int Required(string operation) => operation switch
    {
        "info" => 2, "remove" or "group-remove" => 3, "add" => 6, "group-add" => 5, _ => 4
    };

    internal static bool IsHelp(string[] args) => args.Length == 1 && args[0].Equals("help", StringComparison.OrdinalIgnoreCase);

    internal static void Validate(string[] args, bool mounted = false)
    {
        if (IsHelp(args)) return;
        if (args.Length < 2 || !Usages.TryGetValue(args[0], out string? usage)) throw new CommandUsageException(CommandSyntax.Usages["lp"]);
        int count = Required(args[0].ToLowerInvariant());
        try
        {
            if (args.Length < count || args.Length > count + (mounted ? 0 : 2)) throw new FormatException();
            if (args.Any(string.IsNullOrWhiteSpace)) throw new FormatException();
            if (args.Length > count) CommandSyntax.Lun(args[count]);
            if (args.Length > count + 1 && CommandSyntax.Number(args[count + 1]) > 25) throw new FormatException();
            switch (args[0].ToLowerInvariant())
            {
                case "resize": case "group-resize": CommandSyntax.Number(args[3]); break;
                case "attributes": ParseFlags<LpPartitionAttributes>(args[3]); break;
                case "group-flags": ParseFlags<LpGroupFlags>(args[3]); break;
                case "add": CommandSyntax.Number(args[4]); ParseFlags<LpPartitionAttributes>(args[5]); break;
                case "group-add": CommandSyntax.Number(args[3]); ParseFlags<LpGroupFlags>(args[4]); break;
            }
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        { throw new CommandUsageException(usage + (mounted ? "" : " [lun] [lp-slot]")); }
    }

    private static T ParseFlags<T>(string text) where T : struct, Enum
    {
        T value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? (T)Enum.ToObject(typeof(T), checked((uint)CommandSyntax.Number(text))) : Enum.Parse<T>(text, ignoreCase: true);
        ulong mask = Enum.GetValues<T>().Aggregate(0UL, (current, item) => current | Convert.ToUInt64(item));
        if ((Convert.ToUInt64(value) & ~mask) != 0) throw new FormatException();
        return value;
    }

    internal static async Task ExecuteDeviceAsync(IProtocol protocol, string[] args, ConsoleUi ui,
        IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        Validate(args);
        if (IsHelp(args)) { PrintHelp(ui); return; }
        int count = Required(args[0].ToLowerInvariant());
        string[] mountArgs = [args[1], .. args.Skip(count)];
        using var session = await BrowserCommands.CreateDeviceSessionAsync(protocol, mountArgs, progress, ct).ConfigureAwait(false);
        if (!args[0].Equals("info", StringComparison.OrdinalIgnoreCase) && protocol is IQcomProtocol qcom) FirehoseCommands.Require(qcom, "program");
        string[] operation = args.Take(count).ToArray();
        operation[1] = "/" + operation[1].TrimStart('/');
        await ExecuteMountedAsync(session, operation, ui, ct).ConfigureAwait(false);
    }

    internal static async Task ExecuteMountedAsync(BrowserSession session, string[] args, ConsoleUi ui, CancellationToken ct)
    {
        Validate(args, mounted: true);
        if (IsHelp(args)) { PrintHelp(ui); return; }
        if (args[0].Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            var document = session.GetLpDocument(args[1], ct);
            foreach (var partition in document.Partitions.ToArray())
                ui.WriteLine(Strings.FormatCli_LpPartitionInfo(partition.Name, partition.RawName, partition.LogicalSize,
                    partition.Attributes, document.Groups.Span[checked((int)partition.GroupIndex)].Name));
            foreach (var group in document.Groups.ToArray())
                ui.WriteLine(Strings.FormatCli_LpGroupInfo(group.Name, group.RawName, group.MaximumSize, group.Flags));
            return;
        }
        await session.EditLpAsync(args[1], draft =>
        {
            switch (args[0].ToLowerInvariant())
            {
                case "rename": draft.RenamePartition(draft.FindPartition(args[2]), args[3]); break;
                case "resize": draft.ResizePartition(draft.FindPartition(args[2]), checked((long)CommandSyntax.Number(args[3]))); break;
                case "attributes": draft.SetPartitionAttributes(draft.FindPartition(args[2]), ParseFlags<LpPartitionAttributes>(args[3])); break;
                case "move": draft.MovePartitionToGroup(draft.FindPartition(args[2]), GroupName(draft, args[3])); break;
                case "add": draft.AddPartition(args[2], GroupName(draft, args[3]), ParseFlags<LpPartitionAttributes>(args[5]), checked((long)CommandSyntax.Number(args[4]))); break;
                case "remove": draft.RemovePartition(draft.FindPartition(args[2])); break;
                case "group-add": draft.AddGroup(args[2], CommandSyntax.Number(args[3]), ParseFlags<LpGroupFlags>(args[4])); break;
                case "group-rename": draft.RenameGroup(GroupName(draft, args[2]), args[3]); break;
                case "group-resize": draft.SetGroupMaximumSize(GroupName(draft, args[2]), CommandSyntax.Number(args[3])); break;
                case "group-flags": draft.SetGroupFlags(GroupName(draft, args[2]), ParseFlags<LpGroupFlags>(args[3])); break;
                case "group-remove": draft.RemoveGroup(GroupName(draft, args[2]), LpGroupRemovalMode.FailIfNotEmpty); break;
            }
        }, ui, ct).ConfigureAwait(false);
    }

    private static string GroupName(LpDraft draft, string name) => draft.Groups
        .SingleOrDefault(group => group.Name == name || group.RawName == name)?.RawName ??
        throw new KeyNotFoundException(Strings.FormatCli_BrowserNotFound(name));

    internal static void PrintHelp(ConsoleUi ui)
    {
        ui.WriteHelp(Strings.Cli_LpHelp);
        foreach (var usage in Usages.Values) CommandLine.PrintUsage(usage, ui);
        ui.WriteLine("");
        ui.WriteHelp(Strings.Cli_LpHelpOptions);
    }
}
