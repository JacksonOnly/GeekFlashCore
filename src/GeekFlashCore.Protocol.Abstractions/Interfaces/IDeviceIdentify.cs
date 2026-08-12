using GeekFlashCore.UsbWatcher.Abstractions;

namespace GeekFlashCore.Protocol.Abstractions;

public interface IDeviceIdentify
{
    DeviceProbeResult Identify(UsbDeviceInfo deviceInfo);
}