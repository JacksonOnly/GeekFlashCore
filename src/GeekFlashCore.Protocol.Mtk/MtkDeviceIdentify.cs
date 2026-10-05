using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.UsbWatcher.Abstractions;

namespace GeekFlashCore.Protocol.Mtk;

/// <summary>USB identity candidates only; BROM/DA stage is established by the wire probe.</summary>
public sealed class MtkDeviceIdentify : IDeviceIdentify
{
    public static bool IsSupported(ushort vendorId, ushort productId) => vendorId == 0x0e8d && productId is 3 or 0x2000 or 0x2001 or 0x6000;
    public DeviceProbeResult Identify(UsbDeviceInfo info) => info.VendorId is { } vid && info.ProductId is { } pid &&
        IsSupported((ushort)vid, (ushort)pid) ? DeviceProbeResult.Ok(ProtocolType.Mtk) : DeviceProbeResult.Fail();
}
