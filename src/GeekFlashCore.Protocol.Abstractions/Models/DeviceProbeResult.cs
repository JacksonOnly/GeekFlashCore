namespace GeekFlashCore.Protocol.Abstractions;

public class DeviceProbeResult
{
    public bool IsSuccess { get; set; }
    public ProtocolType ProtocolType { get; set; }

    public static DeviceProbeResult Ok(ProtocolType protocolType)
    {
        return new DeviceProbeResult()
        {
            IsSuccess = true,
            ProtocolType = protocolType,
        };
    }

    public static DeviceProbeResult Fail()
    {
        return new DeviceProbeResult()
        {
            IsSuccess = false
        };
    }
}