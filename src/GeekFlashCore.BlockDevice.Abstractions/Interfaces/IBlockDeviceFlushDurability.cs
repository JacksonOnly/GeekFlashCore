namespace GeekFlashCore.BlockDevice.Abstractions;

public enum BlockDeviceFlushDurability
{
    WriteAccepted = 1,
    ProtocolAcknowledged = 2,
    FlushToDisk = 3
}

public interface IBlockDeviceFlushDurability
{
    BlockDeviceFlushDurability FlushDurability { get; }
}
