using GeekFlashCore.UsbWatcher.Abstractions;
using WmiLight;

namespace GeekFlashCore.UsbWatcher.Internals;

internal class WmiUsbEnumerator : IUsbDeviceEnumerator
{
    // Win32_PnPEntity also retains disconnected devices and their old COM names.
    private const string QueryString = "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%USB%' AND Present = TRUE";

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
