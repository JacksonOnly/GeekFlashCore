using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb;
using GeekFlashCore.Transport.SerialPort;
using GeekFlashCore.UsbWatcher;
using GeekFlashCore.UsbWatcher.Abstractions;
using GeekFlashCore.UsbWatcher.Extensions;

namespace GeekFlashCore.CLI;

internal sealed class TransportResolver
{
    public async Task<ITransport> ResolveAsync(CliOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Port)) return SerialPortTransportFactory.Create(options.Port, options.ReadTimeout, options.WriteTimeout);
        if (options.Usb is { } usb)
        {
            var parts = usb.Split(':', 2);
            if (parts.Length != 2 || !TryHex(parts[0], out int vid) || !TryHex(parts[1], out int pid)) throw new ArgumentException("--usb 格式必须为 VID:PID，例如 05c6:9008");
            return LibUsbTransportFactory.Create(vid, pid);
        }

        var identify = new QcomDeviceIdentify();
        var enumerator = UsbEnumeratorFactory.Create();
        foreach (var device in enumerator.GetDevices())
        {
            if (identify.Identify(device).IsSuccess && device.ExtractPortName() is { } port)
                return SerialPortTransportFactory.Create(port, options.ReadTimeout, options.WriteTimeout);
        }
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("等待 Qualcomm EDL USB 设备热插拔...");
            var monitor = UsbDeviceMonitorFactory.Create();
            try
            {
                var device = await monitor.WaitForDeviceAsync(d => identify.Identify(d).IsSuccess, ct).ConfigureAwait(false);
                var port = device?.ExtractPortName() ?? throw new InvalidOperationException("USB 设备未提供 COM 端口，请使用 --usb VID:PID");
                return SerialPortTransportFactory.Create(port, options.ReadTimeout, options.WriteTimeout);
            }
            finally { if (monitor.IsMonitoring) monitor.StopMonitoring(); }
        }
        throw new InvalidOperationException("未找到设备，请使用 --port 或 --usb VID:PID");
    }

    private static bool TryHex(string text, out int value) => int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
}
