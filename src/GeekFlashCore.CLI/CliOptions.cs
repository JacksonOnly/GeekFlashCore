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
    public string? OplusSign { get; init; }
    public string? LogFile { get; init; }
    public string? OnePlusProjectId { get; init; }
    public OplusDigestMode OplusMode { get; init; }
    public OplusDigestMode EffectiveOplusMode => OplusMode == OplusDigestMode.None && !string.IsNullOrWhiteSpace(OplusDigest)
        ? OplusDigestMode.OplusDigestPt : OplusMode;
    public QcomVendorKind Vendor { get; init; } = QcomVendorKind.Auto;
    public QcomAuthenticationKind? AuthenticationKind { get; init; }
    public bool Verbose { get; init; }
    public bool NonInteractive { get; init; }
    public int ConnectTimeout { get; init; } = QcomProtocolOptions.DefaultConnectTimeoutMilliseconds;
    public int ResourceTimeout { get; init; } = QcomProtocolOptions.DefaultResourceRequestTimeoutMilliseconds;
    public int DeviceWaitTimeout { get; init; } = 30_000;
    public int ReadTimeout { get; init; } = QcomProtocolOptions.DefaultReadTimeoutMilliseconds;
    public int WriteTimeout { get; init; } = QcomProtocolOptions.DefaultWriteTimeoutMilliseconds;
    public string Command { get; init; } = "interactive";
    public string[] Arguments { get; init; } = [];

    public void Validate()
    {
        if (ReadTimeout <= 0 || WriteTimeout <= 0 || ConnectTimeout <= 0 || ResourceTimeout <= 0 || DeviceWaitTimeout <= 0)
            throw new ArgumentException(Localization.Strings.Cli_TimeoutMustBePositive);
        if (!Enum.IsDefined(Vendor) || !Enum.IsDefined(OplusMode) ||
            AuthenticationKind is { } auth && !Enum.IsDefined(auth))
            throw new ArgumentException(Localization.Strings.Cli_EnumInvalid);
        if (Port is not null && Usb is not null)
            throw new ArgumentException(Localization.Strings.Cli_TransportConflict);
        if (Usb is { } usb && !TransportResolver.TryParseUsb(usb, out _, out _))
            throw new ArgumentException(Localization.Strings.Cli_UsbFormatInvalid);
        if (VipChained is not null && VipSigned is null)
            throw new ArgumentException(Localization.Strings.Cli_VipSignedRequired);
        if (OplusSign is not null && OplusMode == OplusDigestMode.None && OplusDigest is null)
            throw new ArgumentException(Localization.Strings.Cli_OplusSignNeedsMode);
        if (NonInteractive && OplusMode == OplusDigestMode.OplusDigestLegacy &&
            (string.IsNullOrWhiteSpace(OplusDigest) || string.IsNullOrWhiteSpace(OplusSign)))
            throw new ArgumentException(Localization.Strings.Cli_LegacyResourcesRequired);
        QcomProtocolHostAdapter.ValidateOptions(this);
    }
}
