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
    private readonly WindowsMtkDriver? _mtkDriver = OperatingSystem.IsWindows()
        ? new(new WindowsMtkDriverBackend()) : null;

    private async Task PrepareNativeUsbAsync(ProtocolRegistration registration, CliOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NativeUsbRuntime.EnsureAvailable();
        if (registration.Type == ProtocolType.Mtk && _mtkDriver is not null)
            await _mtkDriver.EnsureAsync(options.NonInteractive, ct).ConfigureAwait(false);
    }
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
            if (selected.UsbFactory is not null) throw new ArgumentException(Strings.Cli_MtkRequiresUsb);
            return new TransportResolution(SerialPortTransportFactory.Create(options.Port, options.ReadTimeout, options.WriteTimeout), selected);
        }
        if (options.Usb is { } usb)
        {
            if (!TryParseUsb(usb, out int vid, out int pid))
                throw new ArgumentException(Strings.Cli_UsbFormatInvalid);
            if (selected is null && !ProtocolRegistry.TryIdentify(new UsbDeviceInfo { VendorId = vid, ProductId = pid }, out selected))
                throw new ArgumentException(Strings.Cli_ProtocolSelectionRequired);
            await PrepareNativeUsbAsync(selected, options, ct).ConfigureAwait(false);
            return new TransportResolution(selected.UsbFactory is { } factory
                ? factory(new((ushort)vid, (ushort)pid), options)
                : LibUsbTransportFactory.Create(vid, pid, Guid.Empty, readTimeout: options.ReadTimeout, writeTimeout: options.WriteTimeout), selected);
        }

        if (selected?.UsbFactory is not null)
            return await ResolveNativeUsbAsync(selected, options, ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows() && selected is null)
        {
            ProtocolRegistry.TryResolve("mtk", out var mtk);
            return await ResolveNativeUsbAsync(mtk, options, ct).ConfigureAwait(false);
        }
        var enumerator = UsbEnumeratorFactory.Create();
        foreach (var device in enumerator.GetDevices())
        {
            if (ProtocolRegistry.TryIdentify(device, out var identified))
            {
                if (selected is not null && selected.Type != identified.Type) continue;
                var registration = selected ?? identified;
                if (registration.UsbFactory is not null)
                    return await ResolveNativeUsbAsync(registration, options, ct).ConfigureAwait(false);
                if (device.ExtractPortName() is { } port)
                    return new TransportResolution(SerialPortTransportFactory.Create(port, options.ReadTimeout, options.WriteTimeout), registration);
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
                if (device is null) throw new InvalidOperationException(Strings.Cli_DeviceNotFound);
                ProtocolRegistry.TryIdentify(device!, out var identifiedRegistration);
                var registration = selected ?? identifiedRegistration;
                if (registration.UsbFactory is not null)
                    return await ResolveNativeUsbAsync(registration, options, ct).ConfigureAwait(false);
                var port = device.ExtractPortName() ?? throw new InvalidOperationException(Strings.Cli_DeviceMissingComPort);
                return new TransportResolution(SerialPortTransportFactory.Create(port, options.ReadTimeout, options.WriteTimeout), registration);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(Strings.FormatCli_DeviceWaitTimedOut(options.DeviceWaitTimeout));
            }
            finally { if (monitor.IsMonitoring) monitor.StopMonitoring(); }
        }
        throw new InvalidOperationException(Strings.Cli_DeviceNotFound);
    }
    private async Task<TransportResolution> ResolveNativeUsbAsync(ProtocolRegistration registration, CliOptions options, CancellationToken ct)
    {
        NativeUsbRuntime.EnsureAvailable();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (elapsed.ElapsedMilliseconds >= options.DeviceWaitTimeout)
                throw new TimeoutException(Strings.FormatCli_DeviceWaitTimedOut(options.DeviceWaitTimeout));
            // Driver preparation can require UAC and device restart; it has a separate finite budget.
            elapsed.Stop();
            await PrepareNativeUsbAsync(registration, options, ct).ConfigureAwait(false);
            elapsed.Start();
            ct.ThrowIfCancellationRequested();
            var identities = LibUsbTransportFactory.Enumerate(serialNumber: options.UsbSerial).Where(id =>
                registration.DeviceIdentifier?.Identify(new UsbDeviceInfo { VendorId = id.VendorId, ProductId = id.ProductId }).IsSuccess == true);
            var identity = SelectUsbIdentity(identities, options);
            if (identity is not null)
                return new(registration.UsbFactory!(identity, options), registration);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
    internal static UsbTransportIdentity? SelectUsbIdentity(IEnumerable<UsbTransportIdentity> identities, CliOptions options)
    {
        var matches = identities.Where(id => (options.UsbBus is null || options.UsbBus == id.BusNumber) &&
            (options.UsbPortPath is null || options.UsbPortPath == id.PortPath) &&
            (options.UsbSerial is null || options.UsbSerial == id.SerialNumber)).Take(2).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException(Strings.Cli_UsbAmbiguous);
        return matches.SingleOrDefault();
    }

    private static ProtocolRegistration ResolveDefaultRegistration() =>
        ProtocolRegistry.TryResolve(null, out var registration)
            ? registration
            : throw new InvalidOperationException(Strings.Cli_NoProtocolRegistrations);
    internal static bool MatchesDevice(UsbDeviceInfo device, ProtocolRegistration? selected) =>
        ProtocolRegistry.TryIdentify(device, out var identified) &&
        (identified.UsbFactory is not null || device.ExtractPortName() is not null) &&
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
