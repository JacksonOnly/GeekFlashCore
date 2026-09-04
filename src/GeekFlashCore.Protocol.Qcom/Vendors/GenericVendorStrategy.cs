using System.Collections.Frozen;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors;

public sealed class GenericVendorStrategy : IVendorFirehoseStrategy
{
    private static readonly FrozenSet<string> Commands = new[]
    {
        "benchmark", "configure", "emmc", "erase", "firmwarewrite", "fixgpt",
        "getsha256digest", "getstorageinfo", "nop", "patch", "peek", "poke",
        "power", "program", "read", "setbootablestoragedrive", "ufs"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static GenericVendorStrategy Instance { get; } = new();

    private GenericVendorStrategy()
    {
    }

    public QcomVendorKind Vendor => QcomVendorKind.Generic;
    public IReadOnlySet<string> AllowedCustomCommands => Commands;

    public ConfigureCommand PrepareConfigure(ConfigureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command;
    }

    internal static FrozenSet<string> Extend(params string[] commands) =>
        Commands.Concat(commands).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
