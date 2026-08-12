
namespace GeekFlashCore.UsbWatcher.Abstractions;

public interface IUsbDeviceEnumerator
{
    IEnumerable<UsbDeviceInfo> GetDevices();
}