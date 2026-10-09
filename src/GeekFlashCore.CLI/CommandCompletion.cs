using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.CLI;

internal static class CommandCompletion
{
    internal static IReadOnlyList<string> Complete(string prefix, IProtocol protocol, ProtocolRegistration registration)
    {
        var names = CommandSyntax.Usages.Keys.Where(x => x != "interactive").Concat(["exit", "quit"]).ToList();
        if (protocol is IQcomProtocol qcom)
        {
            names.AddRange(FirehoseCommands.MappedCommands(qcom));
            names.AddRange(["qcom", "rawprogram", "patch", "xml", "configure", "probe-sahara"]);
            var reported = qcom.TargetInfo?.Firehose?.BasicDevCharacteristics?.SupportedFunctions;
            if (reported is { Count: > 0 })
            {
                if (!FirehoseCommands.CanProgram(qcom)) names.RemoveAll(x => x is "write" or "program");
                else names.Add("program");
                if (!reported.Contains("read", StringComparer.OrdinalIgnoreCase)) names.RemoveAll(x => x is "read" or "partitions" or "browse" or "ls" or "lp");
                if (!reported.Contains("erase", StringComparer.OrdinalIgnoreCase)) names.Remove("erase");
                if (!reported.Contains("power", StringComparer.OrdinalIgnoreCase)) names.Remove("reboot");
            }
        }
        else
        {
            names.AddRange(registration.CommandHandlers.Select(x => x.Name));
            if (protocol is GeekFlashCore.Protocol.Sprd.Abstractions.ISprdProtocol)
            { names.RemoveAll(x => x is "browse" or "ls" or "lp"); names.Add("sprd-chip-uid"); }
        }
        string[] parts = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (prefix.IndexOf(' ') < 0) return Match(names, prefix);
        string root = parts.FirstOrDefault()?.ToLowerInvariant() ?? "";
        IEnumerable<string> choices = root switch
        {
            "qcom" => FirehoseCommands.Usages.Keys.Select(x => "qcom " + x).Where(x => names.Contains(x[5..])),
            "help" => names.Concat(["all", "qcom", "sprd"]).Select(x => "help " + x),
            "lp" => LpCommands.Usages.Keys.Select(x => "lp " + x),
            "firmware" => new[] { "list", "extract" }.Select(x => "firmware " + x),
            "reboot" => (protocol.Type == ProtocolType.Sprd ? new[] { "system", "poweroff" } : new[] { "system", "download", "poweroff" }).Select(x => "reboot " + x),
            "power" => new[] { "reset", "reset_to_edl", "off" }.Select(x => "power " + x),
            _ => []
        };
        return Match(choices, prefix);
    }

    private static IReadOnlyList<string> Match(IEnumerable<string> values, string prefix) => values
        .Distinct(StringComparer.OrdinalIgnoreCase).Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
