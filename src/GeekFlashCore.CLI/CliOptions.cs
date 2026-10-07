using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed record CliOptions
{
    public string? Port { get; init; }
    public string? Usb { get; init; }
    public string? Protocol { get; init; }
    public string? Loader { get; init; }
    public string? SprdFdl2 { get; init; }
    public uint? SprdFdl1Address { get; init; }
    public uint? SprdFdl2Address { get; init; }
    public GeekFlashCore.Protocol.Sprd.Abstractions.SprdBootStage? SprdEntry { get; init; }
    public long? SprdPartitionUnit { get; init; }
    public GeekFlashCore.Protocol.Sprd.Abstractions.SprdPartitionLengthEncoding? SprdLengthEncoding { get; init; }
    public bool SprdPadOdd { get; init; }
    public bool SprdDisableTranscode { get; init; }
    public bool SprdEntryTranscodeDisabled { get; init; }
    internal bool HasSprdOptions => SprdFdl2 is not null || SprdFdl1Address is not null || SprdFdl2Address is not null ||
        SprdEntry is not null || SprdPartitionUnit is not null || SprdLengthEncoding is not null || SprdPadOdd || SprdDisableTranscode || SprdEntryTranscodeDisabled;
    public string? MtkPreloader { get; init; }
    public string? MtkDaMode { get; init; }
    public string? MtkAuthenticationFile { get; init; }
    public string? MtkCertificateFile { get; init; }
    public bool MtkNandWrite { get; init; }
    public long? MtkNandCapacity { get; init; }
    public bool MtkIoT { get; init; }
    public GeekFlashCore.Protocol.Mtk.Abstractions.MtkExtensionAbi MtkExtensionAbi { get; init; }
    public int MtkNorEraseBlockSize { get; init; }
    public string? MtkPmtLayout { get; init; }
    public string? UsbSerial { get; init; }
    public byte? UsbBus { get; init; }
    public string? UsbPortPath { get; init; }
    public int UsbInterface { get; init; } = -1;
    public int? UsbControlInterface { get; init; }
    public int UsbAlternateSetting { get; init; }
    public IReadOnlyList<uint> MtkUfsRpmbBlocks { get; init; } = [];
    public uint MtkSejBase { get; init; }
    public uint MtkTzccBase { get; init; }
    public uint MtkSsrBase { get; init; }
    public string? Digest { get; init; }
    public string? VipSigned { get; init; }
    public string? VipChained { get; init; }
    public string? OplusDigest { get; init; }
    public string? OplusSign { get; init; }
    public bool OplusResume { get; init; }
    public string? LogFile { get; init; }
    public string? OnePlusProjectId { get; init; }
    public OplusDigestMode OplusMode { get; init; }
    public bool HasExplicitOplusMode { get; init; }
    public OplusDigestMode EffectiveOplusMode => OplusMode == OplusDigestMode.None && !string.IsNullOrWhiteSpace(OplusDigest)
        ? OplusDigestMode.OplusDigestPt : OplusMode;
    public QcomVendorKind Vendor { get; init; } = QcomVendorKind.Auto;
    public QcomAuthenticationKind? AuthenticationKind { get; init; }
    public bool Verbose { get; init; }
    public bool NonInteractive { get; init; }
    public int ConnectTimeout { get; init; } = QcomProtocolOptions.DefaultConnectTimeoutMilliseconds;
    public bool HasExplicitConnectTimeout { get; init; }
    public int? ResourceTimeout { get; init; }
    public int EffectiveResourceTimeout => ResourceTimeout ?? (NonInteractive
        ? QcomProtocolOptions.DefaultResourceRequestTimeoutMilliseconds : Timeout.Infinite);
    public int DeviceWaitTimeout { get; init; } = 30_000;
    public int ReadTimeout { get; init; } = QcomProtocolOptions.DefaultReadTimeoutMilliseconds;
    public int WriteTimeout { get; init; } = QcomProtocolOptions.DefaultWriteTimeoutMilliseconds;
    public string Command { get; init; } = "interactive";
    public string[] Arguments { get; init; } = [];

    public void Validate()
    {
        if (ReadTimeout <= 0 || WriteTimeout <= 0 || ConnectTimeout <= 0 || DeviceWaitTimeout <= 0 ||
            EffectiveResourceTimeout is 0 or < -1)
            throw new ArgumentException(Localization.Strings.Cli_TimeoutMustBePositive);
        if (OplusResume && EffectiveOplusMode == OplusDigestMode.None)
            throw new ArgumentException(Localization.Strings.Cli_OplusResumeNeedsMode);
        if (!Enum.IsDefined(Vendor) || !Enum.IsDefined(OplusMode) ||
            AuthenticationKind is { } auth && !Enum.IsDefined(auth))
            throw new ArgumentException(Localization.Strings.Cli_EnumInvalid);
        if (Port is not null && Usb is not null)
            throw new ArgumentException(Localization.Strings.Cli_TransportConflict);
        if (UsbInterface is < -1 or > 255 || UsbControlInterface is < 0 or > 255 ||
            UsbAlternateSetting is < 0 or > 255 || MtkUfsRpmbBlocks.Count > 4)
            throw new ArgumentException(Localization.Strings.Cli_UsbIdentityInvalid);
        if (Usb is { } usb && !TransportResolver.TryParseUsb(usb, out _, out _))
            throw new ArgumentException(Localization.Strings.Cli_UsbFormatInvalid);
        if (VipChained is not null && VipSigned is null)
            throw new ArgumentException(Localization.Strings.Cli_VipSignedRequired);
        if (OplusSign is not null && OplusMode == OplusDigestMode.None && OplusDigest is null)
            throw new ArgumentException(Localization.Strings.Cli_OplusSignNeedsMode);
        if (NonInteractive && EffectiveOplusMode != OplusDigestMode.None &&
            (string.IsNullOrWhiteSpace(OplusDigest) || string.IsNullOrWhiteSpace(OplusSign)))
            throw new ArgumentException(Localization.Strings.Cli_OplusResourcesRequired);
        bool sprd = Protocol is not null && ProtocolRegistry.TryResolve(Protocol, out var sprdRegistration) &&
            sprdRegistration.Type == GeekFlashCore.Protocol.Abstractions.ProtocolType.Sprd;
        if (sprd) { SprdProtocolHostAdapter.ValidateOptions(this); return; }
        if (HasSprdOptions) throw new ArgumentException(Localization.Strings.Cli_SprdOptionConflict);
        bool mtk = Protocol is not null && ProtocolRegistry.TryResolve(Protocol, out var registration) && registration.Type == GeekFlashCore.Protocol.Abstractions.ProtocolType.Mtk ||
            Protocol is null && Usb is { } identity && TransportResolver.TryParseUsb(identity, out int vid, out int pid) && GeekFlashCore.Protocol.Mtk.MtkDeviceIdentify.IsSupported((ushort)vid, (ushort)pid) ||
            Protocol is null && Command.StartsWith("mtk-", StringComparison.OrdinalIgnoreCase);
        if (mtk) MtkProtocolHostAdapter.ValidateOptions(this);
        else
        {
            if (MtkPreloader is not null || MtkDaMode is not null || MtkAuthenticationFile is not null || MtkCertificateFile is not null ||
                MtkSejBase != 0 || MtkTzccBase != 0 || MtkSsrBase != 0 || MtkUfsRpmbBlocks.Count > 0 ||
                MtkNandWrite || MtkNandCapacity is not null || MtkIoT || MtkExtensionAbi!=0 || MtkNorEraseBlockSize != 0 || MtkPmtLayout is not null ||
                UsbInterface != -1 || UsbControlInterface is not null || UsbAlternateSetting != 0)
                throw new ArgumentException(Localization.Strings.Cli_MtkOptionConflict);
            QcomProtocolHostAdapter.ValidateOptions(this);
        }
    }
}
