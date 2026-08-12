namespace GeekFlashCore.Protocol.Abstractions;

public class DeviceProbeResult
{
    public bool IsSuccess { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public ProtocolType ProtocolType { get; set; }

    public static DeviceProbeResult Ok(ProtocolType protocolType)
    {
        return new DeviceProbeResult()
        {
            IsSuccess = true,
            ProtocolType = protocolType,
        };
    }

    public static DeviceProbeResult Fail(ProtocolType protocolType)
    {
        return new DeviceProbeResult()
        {
            IsSuccess = true,
            ProtocolType = protocolType,
        };
    }
}