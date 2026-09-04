using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors;

public interface IVendorFirehoseStrategy
{
    QcomVendorKind Vendor { get; }
    IReadOnlySet<string> AllowedCustomCommands { get; }
    ConfigureCommand PrepareConfigure(ConfigureCommand command);
}
