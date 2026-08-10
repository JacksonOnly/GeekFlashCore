namespace GeekFlashCore.IO.BlockDevice.Abstractions;

public enum DeviceOwnership
{
    /// <summary>
    /// 相当于LeaveOpen = true 
    /// </summary>
    Borrow,
    /// <summary>
    /// 相当于LeaveOpen = false
    /// </summary>
    Transfer
}
