using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb;
using GeekFlashCore.Transport.SerialPort;
using GeekFlashCore.UsbWatcher;
using GeekFlashCore.UsbWatcher.Extensions;
using GeekFlashCore.UsbWatcher.Abstractions;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal sealed record TransportResolution(ITransport Transport, ProtocolRegistration Registration);

internal sealed class TransportResolver
{
    public async Task<TransportResolution> ResolveAsync(CliOptions options, CancellationToken ct, ProtocolRegistration? preferred = null)
    {
        options.Validate();
        ct.ThrowIfCancellationRequested();
        ProtocolRegistration? selected = preferred;
        if (!string.IsNullOrWhiteSpace(options.Protocol))
        {
            if (!ProtocolRegistry.TryResolve(options.Protocol, out selected))
                throw new ArgumentException(
                    Strings.FormatCli_ProtocolNotRegistered(options.Protocol, ProtocolRegistry.SupportedNames));
        }

        if (!string.IsNullOrWhiteSpace(options.Port))
        {
            selected ??= ResolveDefaultRegistration();
            return new TransportResolution(SerialPortTransportFactory.Create(options.Port, options.ReadTimeout, options.WriteTimeout), selected);
        }
        if (options.Usb is { } usb)
        {
            if (!TryParseUsb(usb, out int vid, out int pid))
                throw new ArgumentException(Strings.Cli_UsbFormatInvalid);
            selected ??= ResolveDefaultRegistration();
            return new TransportResolution(LibUsbTransportFactory.Create(vid, pid, Guid.Empty,
                readTimeout: options.ReadTimeout, writeTimeout: options.WriteTimeout), selected);
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
            Console.WriteLine(selected?.WaitingMessage ?? Strings.Cli_WaitingForDevice);
            var monitor = UsbDeviceMonitorFactory.Create();
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(options.DeviceWaitTimeout);
            try
            {
                var device = await monitor.WaitForDeviceAsync(d => MatchesDevice(d, selected), wait.Token).ConfigureAwait(false);
                var port = device?.ExtractPortName() ??
                    throw new InvalidOperationException(Strings.Cli_DeviceMissingComPort);
                ProtocolRegistry.TryIdentify(device!, out var identifiedRegistration);
                return new TransportResolution(SerialPortTransportFactory.Create(port, options.ReadTimeout, options.WriteTimeout), selected ?? identifiedRegistration);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(Strings.FormatCli_DeviceWaitTimedOut(options.DeviceWaitTimeout));
            }
            finally { if (monitor.IsMonitoring) monitor.StopMonitoring(); }
        }
        throw new InvalidOperationException(Strings.Cli_DeviceNotFound);
    }

    private static ProtocolRegistration ResolveDefaultRegistration() =>
        ProtocolRegistry.TryResolve(null, out var registration)
            ? registration
            : throw new InvalidOperationException(Strings.Cli_NoProtocolRegistrations);
    internal static bool MatchesDevice(UsbDeviceInfo device, ProtocolRegistration? selected) =>
        device.ExtractPortName() is not null && ProtocolRegistry.TryIdentify(device, out var identified) &&
        (selected is null || selected.Type == identified.Type);

    internal static bool TryParseUsb(string usb, out int vid, out int pid)
    {
        vid = pid = 0;
        var parts = usb.Split(':');
        if (parts.Length != 2 || !ushort.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out ushort vendor) ||
            !ushort.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ushort product)) return false;
        vid = vendor;
        pid = product;
        return true;
    }

}
