using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public interface IQcomProtocol : IProtocol,IBlockDeviceProvider
{
    // 待加入一些方法，比如啊 Program Read 等等
}