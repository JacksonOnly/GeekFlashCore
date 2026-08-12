using GeekFlashCore.UsbWatcher.Abstractions;
using GeekFlashCore.UsbWatcher.Internals;

namespace GeekFlashCore.UsbWatcher;

public static class UsbDeviceMonitorFactory
{
    public static IUsbDeviceMonitor Create()
        => new WmiUsbDeviceMonitor();
}