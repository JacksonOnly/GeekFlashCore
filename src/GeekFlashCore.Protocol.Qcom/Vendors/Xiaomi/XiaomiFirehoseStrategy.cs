using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Xiaomi;

public sealed class XiaomiFirehoseStrategy : IVendorFirehoseStrategy
{
    public static XiaomiFirehoseStrategy Instance { get; } = new();

    private XiaomiFirehoseStrategy()
    {
    }

    public QcomVendorKind Vendor => QcomVendorKind.Xiaomi;
    public IReadOnlySet<string> AllowedCustomCommands { get; } = GenericVendorStrategy.Extend("sig");

    public ConfigureCommand PrepareConfigure(ConfigureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command;
    }
}
