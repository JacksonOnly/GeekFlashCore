using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors.OnePlus;

public sealed class OnePlusFirehoseStrategy : IVendorFirehoseStrategy
{
    public static OnePlusFirehoseStrategy Instance { get; } = new();

    private OnePlusFirehoseStrategy()
    {
    }

    public QcomVendorKind Vendor => QcomVendorKind.OnePlus;
    public IReadOnlySet<string> AllowedCustomCommands { get; } = GenericVendorStrategy.Extend(
        "demacia", "setprocstart", "setprocend", "setprojmodel", "setswprojmodel", "SetNetType");

    public ConfigureCommand PrepareConfigure(ConfigureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command;
    }
}
