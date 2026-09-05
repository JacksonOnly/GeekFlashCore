using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed record CliOptions
{
    public string? Port { get; init; }
    public string? Usb { get; init; }
    public string? Protocol { get; init; }
    public string? Loader { get; init; }
    public string? Digest { get; init; }
    public string? VipSigned { get; init; }
    public string? VipChained { get; init; }
    public string? OplusDigest { get; init; }
    public OplusDigestMode OplusMode { get; init; }
    public QcomVendorKind Vendor { get; init; } = QcomVendorKind.Auto;
    public bool Verbose { get; init; }
    public int ReadTimeout { get; init; } = 1000;
    public int WriteTimeout { get; init; } = 1000;
    public string Command { get; init; } = "interactive";
    public string[] Arguments { get; init; } = [];
}
