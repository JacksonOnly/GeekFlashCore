using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.UsbWatcher.Abstractions;

namespace GeekFlashCore.Protocol.Qcom;

public class QcomDeviceIdentify : IDeviceIdentify
{
    public DeviceProbeResult Identify(UsbDeviceInfo deviceInfo)
    {
        ProtocolType protocolType = (deviceInfo.VendorId, deviceInfo.ProductId) switch
        {
            // 9008 is the confirmed Qualcomm EDL product. Other Qualcomm
            // products may expose unrelated interfaces and must be probed by
            // their own protocol identifier.
            (0x05c6, 0x9008) => ProtocolType.QualcommEdl,
            _ => ProtocolType.Unknown
        };
        if(protocolType!= ProtocolType.Unknown)
            return DeviceProbeResult.Ok(protocolType);
        else 
            return DeviceProbeResult.Fail();
    }
}
