using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.UsbWatcher.Abstractions;

namespace GeekFlashCore.Protocol.Sprd;

/// <summary>Identifies confirmed SPRD download USB candidates; the BSL handshake establishes the actual stage.</summary>
public sealed class SprdDeviceIdentify : IDeviceIdentify
{
    /// <summary>Returns whether the exact USB identity is a confirmed download candidate.</summary>
    public static bool IsSupported(int vendorId, int productId) => vendorId == 0x1782 && productId == 0x4d00;

    /// <inheritdoc />
    public DeviceProbeResult Identify(UsbDeviceInfo deviceInfo)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        return deviceInfo.VendorId is { } vid && deviceInfo.ProductId is { } pid && IsSupported(vid, pid)
            ? DeviceProbeResult.Ok(ProtocolType.Sprd) : DeviceProbeResult.Fail();
    }
}
