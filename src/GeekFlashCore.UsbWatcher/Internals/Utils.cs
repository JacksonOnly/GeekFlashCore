using System.Globalization;
using GeekFlashCore.UsbWatcher.Abstractions;
using WmiLight;

namespace GeekFlashCore.UsbWatcher.Internals;

internal static class Utils
{
    private static string? SafeGetString(WmiObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName].ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static UsbDeviceInfo? CreateDeviceInfo(WmiObject instance)
    {
        var deviceId = SafeGetString(instance, "DeviceID");
        if (string.IsNullOrEmpty(deviceId) || !deviceId.Contains("USB", StringComparison.OrdinalIgnoreCase))
            return null;

        return new UsbDeviceInfo
        {
            ClassGuid = SafeGetString(instance, "ClassGuid"),
            HardwareId = deviceId,
            VendorId = ExtractVid(deviceId),
            ProductId = ExtractPid(deviceId),
            FriendlyName = SafeGetString(instance, "Name"),
            Description = SafeGetString(instance, "Description"),
            Manufacturer = SafeGetString(instance, "Manufacturer")
        };
    }

    private static int? ExtractVid(string deviceId)
    {
        var vidIndex = deviceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        int valueIndex = vidIndex + 4;
        if (vidIndex < 0 || deviceId.Length - valueIndex < 4) return null;
        ReadOnlySpan<char> value = deviceId.AsSpan(valueIndex, 4);
        return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int id) ? id : null;
    }

    private static int? ExtractPid(string deviceId)
    {
        var pidIndex = deviceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        int valueIndex = pidIndex + 4;
        if (pidIndex < 0 || deviceId.Length - valueIndex < 4) return null;
        ReadOnlySpan<char> value = deviceId.AsSpan(valueIndex, 4);
        return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int id) ? id : null;
    }
}