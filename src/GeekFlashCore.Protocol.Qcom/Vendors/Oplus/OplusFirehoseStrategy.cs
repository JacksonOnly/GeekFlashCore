using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

public sealed class OplusFirehoseStrategy : IVendorFirehoseStrategy
{
    public static OplusFirehoseStrategy Instance { get; } = new();
    private OplusFirehoseStrategy() { }
    public QcomVendorKind Vendor => QcomVendorKind.Oplus;
    public IReadOnlySet<string> AllowedCustomCommands { get; } =
        GenericVendorStrategy.Extend("digest", "getsigndata", "sha256init", "verify");
    public ConfigureCommand PrepareConfigure(ConfigureCommand command) =>
        command ?? throw new ArgumentNullException(nameof(command));
}
