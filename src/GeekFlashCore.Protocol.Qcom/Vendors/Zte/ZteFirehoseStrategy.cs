using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Zte;

public sealed class ZteFirehoseStrategy : IVendorFirehoseStrategy
{
    public static ZteFirehoseStrategy Instance { get; } = new();

    private ZteFirehoseStrategy()
    {
    }

    public QcomVendorKind Vendor => QcomVendorKind.Zte;
    public IReadOnlySet<string> AllowedCustomCommands { get; } = GenericVendorStrategy.Extend();

    public ConfigureCommand PrepareConfigure(ConfigureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command with { Oem = "ZTE" };
    }
}
