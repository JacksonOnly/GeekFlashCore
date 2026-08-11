
namespace GeekFlashCore.BlockDevice.Abstractions;

public interface IBlockDeviceProvider
{
    IReadOnlyList<BlockDeviceDescriptor> GetBlockDevices();

    IReadableBlockDevice OpenBlockDevice(
        BlockDeviceId id,
        BlockDeviceOpenOptions? options = null);
}
