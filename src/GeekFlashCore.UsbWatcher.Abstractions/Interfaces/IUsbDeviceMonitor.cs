
namespace GeekFlashCore.UsbWatcher.Abstractions;

public interface IUsbDeviceMonitor
{
    event EventHandler<UsbDeviceEventArgs> DeviceAdded;
    event EventHandler<UsbDeviceEventArgs>? DeviceRemoved;

    void StartMonitoring();
    void StopMonitoring();
    bool IsMonitoring { get; }
}