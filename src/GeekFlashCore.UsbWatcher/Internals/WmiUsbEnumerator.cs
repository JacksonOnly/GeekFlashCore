using GeekFlashCore.UsbWatcher.Abstractions;
using WmiLight;

namespace GeekFlashCore.UsbWatcher.Internals;

internal class WmiUsbEnumerator : IUsbDeviceEnumerator
{
    private const string QueryString = "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%USB%'";

    public IEnumerable<UsbDeviceInfo> GetDevices()
    {
        var devices = new List<UsbDeviceInfo>();
        using (WmiConnection con = new WmiConnection())
        {
            foreach (WmiObject process in con.CreateQuery(QueryString))
            {
                var deviceInfo = Utils.CreateDeviceInfo(process);
                if (deviceInfo != null)
                    devices.Add(deviceInfo);
            }
        }

        return devices;
    }
}