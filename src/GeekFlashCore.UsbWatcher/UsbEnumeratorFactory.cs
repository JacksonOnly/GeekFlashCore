using GeekFlashCore.UsbWatcher.Abstractions;
using GeekFlashCore.UsbWatcher.Internals;

namespace GeekFlashCore.UsbWatcher;

public static class UsbEnumeratorFactory
{
    public static IUsbDeviceEnumerator Create()
        => new WmiUsbEnumerator();
}