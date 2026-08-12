namespace GeekFlashCore.UsbWatcher.Abstractions;

public class UsbDeviceEventArgs : EventArgs
{
    public UsbDeviceInfo Device { get; }

    public UsbDeviceEventArgs(UsbDeviceInfo device)
    {
        Device = device;
    }
}