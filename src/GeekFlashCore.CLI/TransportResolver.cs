using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb;
using GeekFlashCore.Transport.SerialPort;
using GeekFlashCore.UsbWatcher;
using GeekFlashCore.UsbWatcher.Extensions;
using GeekFlashCore.UsbWatcher.Abstractions;
using GeekFlashCore.CLI.Localization;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using Serilog;

namespace GeekFlashCore.CLI;

internal sealed record TransportResolution(ITransport Transport, ProtocolRegistration Registration, CliOptions? PreparedOptions = null);

internal sealed class TransportResolver(Func<ProtocolRegistration, CliOptions, CancellationToken, Task<CliOptions>>? prepareOptions = null,
    Func<string, Action, IAsyncDisposable>? beginDeviceWait = null, Action<string>? reportMessage = null,
    Func<IUsbDeviceEnumerator>? createEnumerator = null, Func<IUsbDeviceMonitor>? createMonitor = null)
{
    private int _reportedMtkWaiting;
    private int _reportedMtkRetry;
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
            if (selected.UsbFactory is not null)
                return await ResolveNativeUsbAsync(selected, options, ct).ConfigureAwait(false);
            await PrepareNativeUsbAsync(selected, options, ct).ConfigureAwait(false);
            return new TransportResolution(LibUsbTransportFactory.Create(vid, pid, Guid.Empty,
                readTimeout: options.ReadTimeout, writeTimeout: options.WriteTimeout), selected);
        }

        if (selected?.UsbFactory is not null)
            return await ResolveNativeUsbAsync(selected, options, ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows() && selected is null)
        {
            ProtocolRegistry.TryResolve("mtk", out var mtk);
            return await ResolveNativeUsbAsync(mtk, options, ct).ConfigureAwait(false);
        }
        var enumerator = createEnumerator?.Invoke() ?? UsbEnumeratorFactory.Create();
        foreach (var device in enumerator.GetDevices())
        {
            ct.ThrowIfCancellationRequested();
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
            var monitor = createMonitor?.Invoke() ?? UsbDeviceMonitorFactory.Create();
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(DeviceWaitTimeout(selected, options));
            UsbDeviceInfo? device;
            try
            {
                device = await monitor.WaitForDeviceAsync(d => MatchesDevice(d, selected), wait.Token,
                    autoStart: true, enumerateDevices: enumerator.GetDevices).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(Strings.FormatCli_DeviceWaitTimedOut(options.DeviceWaitTimeout));
            }
            finally { if (monitor.IsMonitoring) monitor.StopMonitoring(); }
            // The monitor's timeout translates only its own wait, not later host input cancellation.
            ct.ThrowIfCancellationRequested();
            if (device is null) throw new InvalidOperationException(Strings.Cli_DeviceNotFound);
            ProtocolRegistry.TryIdentify(device, out var identifiedRegistration);
            var registration = selected ?? identifiedRegistration;
            if (registration.UsbFactory is not null)
                return await ResolveNativeUsbAsync(registration, options, ct).ConfigureAwait(false);
            var port = device.ExtractPortName() ?? throw new InvalidOperationException(Strings.Cli_DeviceMissingComPort);
            return new TransportResolution(SerialPortTransportFactory.Create(port, options.ReadTimeout, options.WriteTimeout), registration);
        }
        throw new InvalidOperationException(Strings.Cli_DeviceNotFound);
    }
    private async Task<TransportResolution> ResolveNativeUsbAsync(ProtocolRegistration registration, CliOptions options, CancellationToken ct)
    {
        // Validate options before acquiring USB; interactive DA selection follows Probe/WDT preparation.
        if (prepareOptions is not null)
            options = await prepareOptions(registration, options, ct).ConfigureAwait(false);
        NativeUsbRuntime.EnsureAvailable();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = waiting.Token;
        await using var activity = registration.Type == ProtocolType.Mtk ? beginDeviceWait?.Invoke(registration.WaitingMessage, waiting.Cancel) : null;
        var preparation = new MtkUsbPreparation();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool reportedDisconnect = false;
        int timeout = DeviceWaitTimeout(registration, options);
        string retryMessage = registration.Type == ProtocolType.Mtk
            ? Strings.Cli_MtkUsbDiscoveryRetry : Strings.Cli_UsbOpenDisconnectedRetry;
        if (registration.Type == ProtocolType.Mtk && activity is null && Interlocked.Exchange(ref _reportedMtkWaiting, 1) == 0)
            Console.WriteLine(registration.WaitingMessage);
        void ReportRetry(Exception? exception = null)
        {
            if (exception is not null) Log.Debug(exception, retryMessage);
            if (reportedDisconnect) return;
            if (registration.Type == ProtocolType.Mtk && Interlocked.Exchange(ref _reportedMtkRetry, 1) != 0) return;
            if (reportMessage is null) Console.WriteLine(retryMessage); else reportMessage(retryMessage);
            reportedDisconnect = true;
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (timeout != Timeout.Infinite && elapsed.ElapsedMilliseconds >= timeout)
                throw DeviceWaitExpired(registration, options);
            // MTK explicit admission includes preparation; preserve other protocols' old discovery budget.
            if (registration.Type != ProtocolType.Mtk) elapsed.Stop();
            if (registration.Type == ProtocolType.Mtk)
            {
                try { await preparation.EnsureAsync(token => PrepareNativeUsbAsync(registration, options, token), ct).ConfigureAwait(false); }
                catch (Exception exception) when (MtkConnectionAdmission.IsUsbFailure(exception))
                {
                    ct.ThrowIfCancellationRequested();
                    ReportRetry(exception);
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    continue;
                }
            }
            else await PrepareNativeUsbAsync(registration, options, ct).ConfigureAwait(false);
            if (registration.Type != ProtocolType.Mtk) elapsed.Start();
            ct.ThrowIfCancellationRequested();
            if (timeout != Timeout.Infinite && elapsed.ElapsedMilliseconds >= timeout)
                throw DeviceWaitExpired(registration, options);
            IReadOnlyList<UsbTransportIdentity> enumerated;
            try { enumerated = LibUsbTransportFactory.Enumerate(registration.Type == ProtocolType.Mtk ? (ushort?)0x0e8d : null, serialNumber: options.UsbSerial); }
            catch (Exception exception) when (registration.Type == ProtocolType.Mtk && IsMtkUsbDiscoveryFailure(exception))
            {
                ct.ThrowIfCancellationRequested();
                ReportRetry(exception);
                await Task.Delay(100, ct).ConfigureAwait(false);
                continue;
            }
            var identities = enumerated.Where(id =>
                registration.DeviceIdentifier?.Identify(new UsbDeviceInfo { VendorId = id.VendorId, ProductId = id.ProductId }).IsSuccess == true);
            var identity = SelectUsbIdentity(identities, options);
            if (identity is not null)
            {
                var transport = registration.Type == ProtocolType.Mtk
                    ? TryOpenMtkTransport(() => registration.UsbFactory!(identity, options), ct)
                    : TryOpenNativeTransport(() => registration.UsbFactory!(identity, options), ct);
                if (transport is not null)
                {
                    try
                    {
                        if (activity is not null) await activity.DisposeAsync().ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (timeout != Timeout.Infinite && elapsed.ElapsedMilliseconds >= timeout)
                            throw DeviceWaitExpired(registration, options);
                        return new(transport, registration, options);
                    }
                    catch { transport.Dispose(); throw; }
                }
                ReportRetry();
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
    internal static int DeviceWaitTimeout(ProtocolRegistration? registration, CliOptions options) =>
        !options.HasExplicitDeviceWaitTimeout && (registration is null || registration.Type == ProtocolType.Mtk)
            ? Timeout.Infinite : options.DeviceWaitTimeout;

    private static TimeoutException DeviceWaitExpired(ProtocolRegistration registration, CliOptions options) =>
        registration.Type == ProtocolType.Mtk ? new MtkDeviceWaitTimeoutException(options.DeviceWaitTimeout)
            : new TimeoutException(Strings.FormatCli_DeviceWaitTimedOut(options.DeviceWaitTimeout));

    private static bool IsMtkUsbDiscoveryFailure(Exception exception) =>
        exception is UsbException or IOException or InvalidOperationException;

    internal static ITransport? TryOpenMtkTransport(Func<ITransport> create, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ITransport? transport = null;
        try
        {
            transport = create();
            ct.ThrowIfCancellationRequested();
            transport.Open();
            ct.ThrowIfCancellationRequested();
            return transport;
        }
        catch (Exception exception) when (IsMtkUsbDiscoveryFailure(exception))
        {
            try { transport?.Dispose(); }
            catch (Exception cleanup) { Log.Debug(cleanup, Strings.Cli_MtkUsbDiscoveryRetry); }
            ct.ThrowIfCancellationRequested();
            Log.Debug(exception, Strings.Cli_MtkUsbDiscoveryRetry);
            return null;
        }
        catch { transport?.Dispose(); throw; }
    }
    internal static UsbTransportIdentity? SelectUsbIdentity(IEnumerable<UsbTransportIdentity> identities, CliOptions options)
    {
        var matches = identities.Where(id => (options.Usb is null || TryParseUsb(options.Usb, out int vid, out int pid) && id.VendorId == vid && id.ProductId == pid) &&
            (options.UsbBus is null || options.UsbBus == id.BusNumber) &&
            (options.UsbPortPath is null || options.UsbPortPath == id.PortPath) &&
            (options.UsbSerial is null || options.UsbSerial == id.SerialNumber)).Take(2).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException(Strings.Cli_UsbAmbiguous);
        return matches.SingleOrDefault();
    }

    internal static ITransport? TryOpenNativeTransport(Func<ITransport> create, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ITransport? transport = null;
        try
        {
            transport = create();
            ct.ThrowIfCancellationRequested();
            transport.Open();
            ct.ThrowIfCancellationRequested();
            return transport;
        }
        catch (UsbException exception) when (exception.ErrorCode == Error.NoDevice)
        {
            transport?.Dispose();
            ct.ThrowIfCancellationRequested();
            return null;
        }
        catch { transport?.Dispose(); throw; }
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
