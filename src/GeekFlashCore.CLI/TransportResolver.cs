using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb;
using GeekFlashCore.Transport.SerialPort;
using GeekFlashCore.UsbWatcher;
using GeekFlashCore.UsbWatcher.Extensions;

namespace GeekFlashCore.CLI;

internal sealed record TransportResolution(ITransport Transport, ProtocolRegistration Registration);

internal sealed class TransportResolver
{
    public async Task<TransportResolution> ResolveAsync(CliOptions options, CancellationToken ct, ProtocolRegistration? preferred = null)
    {
        ProtocolRegistration? selected = preferred;
        if (!string.IsNullOrWhiteSpace(options.Protocol))
        {
            if (!ProtocolRegistry.TryResolve(options.Protocol, out selected))
                throw new ArgumentException($"协议 '{options.Protocol}' 当前未注册；可用协议：{ProtocolRegistry.SupportedNames}");
        }

        if (!string.IsNullOrWhiteSpace(options.Port))
        {
            selected ??= ResolveDefaultRegistration();
            return new TransportResolution(SerialPortTransportFactory.Create(options.Port, options.ReadTimeout, options.WriteTimeout), selected);
        }
        if (options.Usb is { } usb)
        {
            var parts = usb.Split(':', 2);
            if (parts.Length != 2 || !TryHex(parts[0], out int vid) || !TryHex(parts[1], out int pid))
                throw new ArgumentException("--usb 格式必须为 VID:PID，例如 05c6:9008");
            selected ??= ResolveDefaultRegistration();
            return new TransportResolution(LibUsbTransportFactory.Create(vid, pid), selected);
        }

        var enumerator = UsbEnumeratorFactory.Create();
        foreach (var device in enumerator.GetDevices())
        {
            if (device.ExtractPortName() is not { } port) continue;
            if (ProtocolRegistry.TryIdentify(device, out var identified))
            {
                if (selected is not null && selected.Type != identified.Type) continue;
                return new TransportResolution(SerialPortTransportFactory.Create(port, options.ReadTimeout, options.WriteTimeout), selected ?? identified);
            }
        }
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine(selected?.WaitingMessage ?? "等待已注册协议设备热插拔...");
            var monitor = UsbDeviceMonitorFactory.Create();
            try
            {
                var device = await monitor.WaitForDeviceAsync(d => selected is not null
                    ? d.ExtractPortName() is not null
                    : ProtocolRegistry.TryIdentify(d, out _), ct).ConfigureAwait(false);
                var port = device?.ExtractPortName() ?? throw new InvalidOperationException("USB 设备未提供 COM 端口，请使用 --usb VID:PID");
                ProtocolRegistry.TryIdentify(device!, out var identifiedRegistration);
                return new TransportResolution(SerialPortTransportFactory.Create(port, options.ReadTimeout, options.WriteTimeout), selected ?? identifiedRegistration);
            }
            finally { if (monitor.IsMonitoring) monitor.StopMonitoring(); }
        }
        throw new InvalidOperationException("未找到设备，请使用 --port 或 --usb VID:PID");
    }

    private static ProtocolRegistration ResolveDefaultRegistration() => ProtocolRegistry.TryResolve(null, out var registration) ? registration : throw new InvalidOperationException("没有可用协议注册项");
    private static bool TryHex(string text, out int value) => int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
}
