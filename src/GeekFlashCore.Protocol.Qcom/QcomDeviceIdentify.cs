using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.UsbWatcher.Abstractions;

namespace GeekFlashCore.Protocol.Qcom;

public class QcomDeviceIdentify : IDeviceIdentify
{
    public DeviceProbeResult Identify(UsbDeviceInfo deviceInfo)
    {
        ProtocolType protocolType = (deviceInfo.VendorId, deviceInfo.ProductId) switch
        {
            // 暂时就直接这样子了，之后会添加的。。。
            (0x05c6,_) => ProtocolType.QualcommEdl,
            _ => ProtocolType.Unknown
        };
        if(protocolType!= ProtocolType.Unknown)
            return DeviceProbeResult.Ok(protocolType);
        else 
            return DeviceProbeResult.Fail();
    }
}