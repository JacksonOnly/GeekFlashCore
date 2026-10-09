using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb;
using GeekFlashCore.UsbWatcher;
using GeekFlashCore.UsbWatcher.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed partial class CliApplication
{
    private readonly ConsoleUi _ui;
    private readonly TransportResolver _transportResolver;
    private readonly IProgress<ProgressRecord> _progress;
    private CliOptions? _sessionOptions;
    private MtkReconnectSnapshot? _reconnectSnapshot;
    private bool _cleanReconnectBoundary;
    private string[] _reconnectArguments = [];

    public CliApplication(ConsoleUi? ui = null)
    {
        _ui = ui ?? new ConsoleUi();
        _transportResolver = new(PrepareConnectionOptionsAsync, _ui.BeginDeviceWait, _ui.WriteLine);
        _progress = new ImmediateProgress<ProgressRecord>(_ui.Report);
    }

    public async Task<int> RunAsync(CliOptions options, CancellationToken ct)
    {
        options.Validate();
        _sessionOptions = options;
        if (options.Command == "reconnect") _reconnectArguments = options.Arguments;
        _ui.AllowPrompts = !options.NonInteractive;
        _ui.WriteBanner();
        if (options.Command != "help" && _ui.LogFilePath is not null)
            _ui.WriteLine(Strings.FormatCli_LogLocation(_ui.LogFilePath));
        ProtocolRegistration? requestedRegistration = null;
        if (!string.IsNullOrWhiteSpace(options.Protocol) && !ProtocolRegistry.TryResolve(options.Protocol, out requestedRegistration))
            throw new ArgumentException(
                Strings.FormatCli_ProtocolNotRegistered(options.Protocol, ProtocolRegistry.SupportedNames));
        if (requestedRegistration is null && !options.Command.Equals("interactive", StringComparison.OrdinalIgnoreCase))
            ProtocolRegistry.TryResolveCommand(options.Command, out requestedRegistration);
        try { options = NormalizeAndValidate(options, requestedRegistration); }
        catch (CommandUsageException exception) { _ui.WriteLine(exception.Message); return 2; }
        if (options.Command == "help") { CommandLine.PrintRequestedHelp(options.Arguments, _ui); return 0; }
        if (MtkScatterCommands.IsOffline(options)) { await MtkScatterCommands.ExecuteOfflineAsync(options, _ui, ct); return 0; }
        if (options.Command == "firmware") { FirmwareCommands.Execute(options.Arguments, _ui, ct); return 0; }
        if (options.Command == "lp" && LpCommands.IsHelp(options.Arguments)) { LpCommands.PrintHelp(_ui); return 0; }
        if (options.Command is "browse" or "browse-image" && BrowserCommands.ShowHelp(options.Arguments, _ui)) return 0;
        if (options.Command == "browse-image")
        {
            try { await BrowserCommands.BrowseImageAsync(options.Arguments, _ui, ct).ConfigureAwait(false); return 0; }
            catch (OperationCanceledException) { _ui.ShowCancelled(); return 130; }
            catch (Exception exception) { _ui.LogException(exception); return 1; }
        }
        if (options.Command.Equals("devices", StringComparison.OrdinalIgnoreCase)) return ListDevices(options, requestedRegistration);
        if (options.Command.Equals("interactive", StringComparison.OrdinalIgnoreCase))
            return await InteractiveAsync(options, ct).ConfigureAwait(false);

        var connection = await CreateConnectionAsync(options, requestedRegistration, ct).ConfigureAwait(false);
        using ITransport transport = connection.Transport;
        await using var protocol = connection.Protocol;
        try
        {
            var progress = _progress;
            if (connection.Registration.CommandSet is { } commands && commands.Handles(options.Command) && !commands.RequiresConnection(options.Command))
                return await ExecuteCommandAsync(protocol, connection.Registration, options, progress, ct);
            IProtocolCommandHandler? special = FindSpecialHandler(connection.Registration, options);
            if (special is not null && !special.RequiresConnection(options.Arguments))
                return await special.ExecuteAsync(protocol, options.Arguments, _ui, progress, ct).ConfigureAwait(false);

            _ui.WriteLine(Strings.Cli_Connecting);
            if (!protocol.IsConnected)
            {
                await protocol.ConnectAsync(progress, ct).ConfigureAwait(false);
                MtkProtocolHostAdapter.InitializeExtension(protocol, options, ct);
            }
            return await ExecuteCommandAsync(protocol, connection.Registration, options, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { _ui.ShowCancelled(); EndMtkOperation(protocol); return 130; }
        catch (Exception exception) { _ui.LogException(exception); EndMtkOperation(protocol); return 1; }
    }

    private async Task<int> InteractiveAsync(CliOptions options, CancellationToken ct)
    {
        bool recover = IsMtkConnection(options);
        while (true)
        {
            ActiveConnection? active = null;
            int failure;
            try
            {
                using (var search = recover ? _ui.BeginSearch(ct) : null)
                {
                    CancellationToken connecting = search?.Token ?? ct;
                    var connection = await CreateConnectionAsync(options, null, connecting).ConfigureAwait(false);
                    active = new(connection.Protocol, connection.Transport, connection.Registration);
                    recover |= active.Protocol.Type == ProtocolType.Mtk;
                    _ui.WriteLine(Strings.Cli_Connecting);
                    if (!active.Protocol.IsConnected)
                    {
                        await active.Protocol.ConnectAsync(_progress, connecting).ConfigureAwait(false);
                        MtkProtocolHostAdapter.InitializeExtension(active.Protocol, options, connecting);
                    }
                }
                _ui.WriteLine(Strings.FormatCli_ConnectedHelp(active.Registration.DisplayName));
                ShowQcomCommands(active.Protocol);
                return await ReadCommandsAsync(active, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { _ui.ShowCancelled(); failure = 130; }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { _ui.LogException(exception); failure = 1; }
            finally
            {
                if (active is not null)
                {
                    try { await active.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) when (active.Protocol.Type == ProtocolType.Mtk)
                    { Serilog.Log.Debug(cleanup, Strings.Cli_MtkOperationStopped); }
                }
            }
            if (!recover || !_ui.CanPrompt) return failure;
            var retry = await WaitForMtkRetryAsync(options, ct).ConfigureAwait(false);
            if (retry is null) return 0;
            options = retry; _reconnectArguments = retry.Arguments;
            _cleanReconnectBoundary = false;
            _sessionOptions = retry with { Command = "interactive", Arguments = [] };
        }
    }

    private async Task<int> ReadCommandsAsync(ActiveConnection active, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string line = await _ui.ReadCommandAsync("geekflash> ",
                prefix => CommandCompletion.Complete(prefix, active.Protocol, active.Registration), ct).ConfigureAwait(false) ?? "exit";
            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) || line.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
            try
            {
                var defaults = _sessionOptions ?? new CliOptions();
                if (active.Protocol.Type == ProtocolType.Mtk) defaults = defaults with { Protocol = "mtk" };
                var parsed = CommandLine.ParseSession(Tokenize(line), defaults);
                if (parsed.Command.Equals("interactive", StringComparison.OrdinalIgnoreCase)) continue;
                if (parsed.Command == "reconnect" || parsed.Command == "connect" && !active.Protocol.IsConnected && active.Protocol.Type == ProtocolType.Mtk)
                {
                    CommandSyntax.Validate(parsed);
                    if (active.Protocol.Type != ProtocolType.Mtk) throw new CommandUsageException("reconnect (MTK)");
                    using var search = _ui.BeginSearch(ct);
                    CancellationToken connecting = search.Token;
                    _reconnectSnapshot = MtkProtocolHostAdapter.Capture(active.Protocol) ?? _reconnectSnapshot;
                    _cleanReconnectBoundary = active.Protocol.IsConnected;
                    _reconnectArguments = parsed.Arguments;
                    if (_reconnectSnapshot is { } snapshot)
                        parsed = parsed with { Protocol = "mtk", Usb = null,
                            UsbSerial = snapshot.Identity.SerialNumber, UsbBus = snapshot.Identity.BusNumber, UsbPortPath = snapshot.Identity.PortPath };
                    await active.ReleaseAsync();
                    var next = await CreateConnectionAsync(parsed, active.Registration, connecting).ConfigureAwait(false);
                    active.Replace(next.Protocol, next.Transport, next.Registration);
                    if (!active.Protocol.IsConnected)
                    {
                        await active.Protocol.ConnectAsync(_progress, connecting).ConfigureAwait(false);
                        MtkProtocolHostAdapter.InitializeExtension(active.Protocol, parsed, connecting);
                    }
                    _sessionOptions = parsed with { Command = "interactive", Arguments = [] };
                    _ui.WriteLine(Strings.FormatCli_ConnectedHelp(active.Registration.DisplayName));
                    continue;
                }
                _reconnectSnapshot = MtkProtocolHostAdapter.Capture(active.Protocol) ?? _reconnectSnapshot;
                int result = await ExecuteCommandAsync(active.Protocol, active.Registration, parsed, _progress, ct).ConfigureAwait(false);
                bool endsSession = parsed.Command is "reboot" or "power" ||
                    result == 0 && !active.Protocol.IsConnected ||
                    parsed.Command == "qcom" && parsed.Arguments.Length > 0 &&
                    parsed.Arguments[0].Equals("power", StringComparison.OrdinalIgnoreCase);
                if (result == 0 && endsSession)
                {
                    _ui.WriteLine(Strings.Cli_SessionEnded);
                    if (active.Protocol.Type != ProtocolType.Mtk) return 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { EndMtkOperation(active.Protocol); throw; }
            catch (OperationCanceledException) { _ui.ShowCancelled(); EndMtkOperation(active.Protocol); }
            catch (Exception exception) { _ui.LogException(exception); EndMtkOperation(active.Protocol); }
        }
        return 0;
    }

    private bool EndMtkOperation(IProtocol protocol)
    {
        if (protocol is not GeekFlashCore.Protocol.Mtk.Abstractions.IMtkProtocol mtk) return false;
        _ui.StopMtkProgress();
        if (mtk.SessionState != GeekFlashCore.Protocol.Mtk.Abstractions.MtkSessionState.Faulted) return false;
        _ui.WriteLine(Strings.Cli_MtkOperationStopped);
        return true;
    }

    private async Task<int> ExecuteCommandAsync(IProtocol protocol, ProtocolRegistration registration, CliOptions options, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        try { options = NormalizeAndValidate(options, registration); }
        catch (CommandUsageException exception) { _ui.WriteLine(exception.Message); return 2; }
        ct.ThrowIfCancellationRequested();
        if (MtkScatterCommands.IsOffline(options)) { await MtkScatterCommands.ExecuteOfflineAsync(options, _ui, ct); return 0; }
        if (options.Command == "firmware") { FirmwareCommands.Execute(options.Arguments, _ui, ct); return 0; }
        if (options.Command == "lp" && LpCommands.IsHelp(options.Arguments)) { LpCommands.PrintHelp(_ui); return 0; }
        if (options.Command is "browse" or "browse-image" && BrowserCommands.ShowHelp(options.Arguments, _ui)) return 0;
        registration.CommandSet?.ValidateAvailability(protocol, options.Command);
        if (registration.CommandSet is { } commands && commands.Handles(options.Command))
            return await commands.ExecuteAsync(protocol, options, _ui, progress, ct);
        IProtocolCommandHandler? special = FindSpecialHandler(registration, options);
        if (options.Command.Equals(special?.Name, StringComparison.OrdinalIgnoreCase) && special is not null)
            return await special.ExecuteAsync(protocol, options.Arguments, _ui, progress, ct).ConfigureAwait(false);

        switch (options.Command.ToLowerInvariant())
        {
            case "reconnect":
                if (protocol.Type != ProtocolType.Mtk) throw new CommandUsageException("reconnect (MTK)");
                return 0;
            case "connect":
                if (protocol.IsConnected) return 0;
                _ui.WriteLine(Strings.Cli_Connecting);
                await protocol.ConnectAsync(progress, ct).ConfigureAwait(false);
                MtkProtocolHostAdapter.InitializeExtension(protocol, options, ct);
                _ui.WriteLine(Strings.FormatCli_ConnectedHelp(registration.DisplayName));
                ShowQcomCommands(protocol);
                return 0;
            case "info":
                registration.InfoPresenter?.Invoke(protocol, _ui);
                ShowQcomCommands(protocol);
                return 0;
            case "browse":
                await BrowserCommands.BrowseDeviceAsync(protocol, options.Arguments, _ui, progress, ct).ConfigureAwait(false);
                return 0;
            case "ls":
                await BrowserCommands.ListDeviceAsync(protocol, options.Arguments, _ui, progress, ct).ConfigureAwait(false);
                return 0;
            case "lp":
                await LpCommands.ExecuteDeviceAsync(protocol, options.Arguments, _ui, progress, ct).ConfigureAwait(false);
                return 0;
            case "browse-image":
                await BrowserCommands.BrowseImageAsync(options.Arguments, _ui, ct).ConfigureAwait(false);
                return 0;
            case "partitions": case "read": case "write": case "erase":
                await StorageCommands.ExecuteAsync(protocol, options.Command,
                    options.Arguments, _ui, progress, ct);
                return 0;
            case "reboot":
                var mode = Enum.Parse<ProtocolRebootMode>(options.Arguments[0], true);
                if (!await protocol.RebootAsync(mode, progress, ct).ConfigureAwait(false))
                    throw new InvalidOperationException(Strings.FormatCli_CommandUnsuccessful("reboot"));
                return 0;
            case "help":
                if (options.Arguments.Length == 1 && options.Arguments[0].Equals("qcom", StringComparison.OrdinalIgnoreCase) && protocol is GeekFlashCore.Protocol.Qcom.Abstractions.IQcomProtocol qcom)
                    FirehoseCommands.PrintDetails(qcom, _ui);
                else
                {
                    CommandLine.PrintRequestedHelp(options.Arguments, _ui);
                    if (options.Arguments.Length == 0) registration.CommandSet?.PrintHelp(protocol, _ui);
                }
                return 0;
            default: throw new ArgumentException(Strings.FormatCli_UnknownCommand(options.Command));
        }
    }

    private async Task<(IProtocol Protocol, ITransport Transport, ProtocolRegistration Registration)> CreateConnectionAsync(CliOptions options, ProtocolRegistration? requested, CancellationToken ct)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool boundedMtkDiscovery = options.HasExplicitDeviceWaitTimeout &&
            (requested is null || requested.Type == ProtocolType.Mtk);
        if (boundedMtkDiscovery) discovery.CancelAfter(options.DeviceWaitTimeout);
        TransportResolution resolution;
        try { resolution = await _transportResolver.ResolveAsync(options, discovery.Token, requested).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && discovery.IsCancellationRequested)
        { throw new MtkDeviceWaitTimeoutException(options.DeviceWaitTimeout); }
        discovery.CancelAfter(Timeout.Infinite);
        ProtocolRegistration registration = requested ?? resolution.Registration;
        if (registration.Type == ProtocolType.Mtk)
        {
            bool daCandidate = resolution.Transport is IUsbTransport usb && usb.Identity.ProductId == 0x2001;
            if (RequiresMtkLoader(registration, options) && (daCandidate || _reconnectArguments.FirstOrDefault() is "da1" or "da2" ||
                _reconnectSnapshot is not null && _reconnectArguments.FirstOrDefault() != "brom"))
            {
                IProtocol? resumed = null;
                try
                {
                    resumed = registration.Factory(new ProtocolHostContext(_ui, options), resolution.Transport);
                    await MtkProtocolHostAdapter.ResumeAsync((GeekFlashCore.Protocol.Mtk.MtkProtocol)resumed, options, _ui,
                        _reconnectSnapshot, _cleanReconnectBoundary, _reconnectArguments, ct).ConfigureAwait(false);
                    return (resumed, resolution.Transport, registration);
                }
                catch
                {
                    try { if (resumed is not null) await resumed.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { Serilog.Log.Debug(cleanup, Strings.Cli_MtkOperationStopped); }
                    try { resolution.Transport.Dispose(); }
                    catch (Exception cleanup) { Serilog.Log.Debug(cleanup, Strings.Cli_MtkOperationStopped); }
                    throw;
                }
            }
            int? remaining = options.HasExplicitDeviceWaitTimeout
                ? (int)Math.Max(0, options.DeviceWaitTimeout - elapsed.ElapsedMilliseconds) : null;
            TransportResolution? candidate = resolution;
            ITransport? acceptedTransport = null;
            IProtocol accepted;
            try
            {
                if (remaining == 0) throw new MtkDeviceWaitTimeoutException(options.DeviceWaitTimeout);
                accepted = await MtkConnectionAdmission.WaitAsync(async (admitted, token) =>
                {
                    var current = candidate ?? await _transportResolver.ResolveAsync(options, token, registration).ConfigureAwait(false);
                    candidate = null;
                    var protocol = await CreateProtocolCoreAsync(registration, current.Transport,
                        current.PreparedOptions ?? options, token, admitted).ConfigureAwait(false);
                    acceptedTransport = current.Transport;
                    return protocol;
                }, remaining, () => _ui.WriteLine(Strings.Cli_MtkInitialProbeRetry), ct).ConfigureAwait(false);
            }
            catch (MtkDeviceWaitTimeoutException exception)
            { throw new MtkDeviceWaitTimeoutException(options.DeviceWaitTimeout, exception); }
            finally
            {
                try { candidate?.Transport.Dispose(); }
                catch (Exception cleanup) { Serilog.Log.Debug(cleanup, Strings.Cli_MtkOperationStopped); }
            }
            return (accepted, acceptedTransport!, registration);
        }
        IProtocol protocol = await CreateProtocolAsync(registration, resolution.Transport,
            resolution.PreparedOptions ?? options, ct).ConfigureAwait(false);
        return (protocol, resolution.Transport, registration);
    }

    internal Task<IProtocol> CreateProtocolAsync(ProtocolRegistration registration, ITransport transport,
        CliOptions options, CancellationToken ct) => CreateProtocolCoreAsync(registration, transport, options, ct, null);

    private async Task<IProtocol> CreateProtocolCoreAsync(ProtocolRegistration registration, ITransport transport,
        CliOptions options, CancellationToken ct, Action? admitted)
    {
        IProtocol? protocol = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var context = new ProtocolHostContext(_ui, options);
            if (RequiresMtkLoader(registration, options) && _ui.CanPrompt)
                return await MtkProtocolHostAdapter.CreatePreparedAsync(context, transport, ct, admitted).ConfigureAwait(false);
            protocol = registration.Factory(context, transport);
            if (admitted is not null)
            {
                if (options.Command != "mtk-capabilities")
                {
                    var mtk = (GeekFlashCore.Protocol.Mtk.MtkProtocol)protocol;
                    if (options.Command == "reconnect" && options.Arguments.FirstOrDefault() is null or "auto" &&
                        mtk.InspectEntrySignal(ct) is GeekFlashCore.Protocol.Mtk.Abstractions.MtkEntrySignal.Da1Sync or GeekFlashCore.Protocol.Mtk.Abstractions.MtkEntrySignal.FramedDa)
                        await MtkProtocolHostAdapter.ResumeAsync(mtk, options, _ui, null, false, [], ct).ConfigureAwait(false);
                    else MtkProtocolHostAdapter.ProbeForAdmission(mtk, ct);
                }
                admitted();
            }
            return protocol;
        }
        catch
        {
            if (registration.Type != ProtocolType.Mtk) { transport.Dispose(); throw; }
            try { if (protocol is not null) await protocol.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { Serilog.Log.Debug(cleanup, Strings.Cli_MtkOperationStopped); }
            try { transport.Dispose(); }
            catch (Exception cleanup) { Serilog.Log.Debug(cleanup, Strings.Cli_MtkOperationStopped); }
            throw;
        }
    }

    private static bool RequiresMtkLoader(ProtocolRegistration registration, CliOptions options) =>
        registration.Type == ProtocolType.Mtk && options.Command is not ("help" or "devices") &&
        !(registration.CommandSet is { } commands && commands.Handles(options.Command) && !commands.RequiresConnection(options.Command));

    internal Task<CliOptions> PrepareConnectionOptionsAsync(ProtocolRegistration registration, CliOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (registration.Type != ProtocolType.Mtk) return Task.FromResult(options);
        MtkProtocolHostAdapter.ValidateOptions(options);
        // Interactive selection must follow Probe/WDT initialization on the acquired transport.
        // Noninteractive validation can still fail before acquiring any USB resources.
        if (!RequiresMtkLoader(registration, options) || _ui.CanPrompt)
            return Task.FromResult(options);
        return MtkProtocolHostAdapter.SelectLoaderAsync(options, _ui, ct);
    }

    private static CliOptions NormalizeAndValidate(CliOptions options, ProtocolRegistration? registration)
    {
        options = CommandSyntax.Normalize(registration?.CommandSet?.Normalize(options) ?? options);
        if (CommandSyntax.Usages.ContainsKey(options.Command)) CommandSyntax.Validate(options);
        else if (registration?.CommandSet is { } commands && commands.Handles(options.Command)) commands.Validate(options);
        else if (registration is null || FindSpecialHandler(registration, options) is null) throw new CommandUsageException(options.Command + " (help)");
        return options;
    }

    private static IProtocolCommandHandler? FindSpecialHandler(ProtocolRegistration registration, CliOptions options) =>
        registration.CommandHandlers.FirstOrDefault(handler => handler.Name.Equals(options.Command, StringComparison.OrdinalIgnoreCase) && handler.Handles(options.Arguments));

    private void ShowQcomCommands(IProtocol protocol)
    {
        if (protocol is not GeekFlashCore.Protocol.Qcom.Abstractions.IQcomProtocol) return;
        FirehoseCommands.PrintMapping(protocol, _ui);
        _ui.WriteLine(Strings.Cli_CommonCommandsSummary);
    }

    private int ListDevices(CliOptions options, ProtocolRegistration? registration)
    {
        try
        {
            if (registration?.UsbFactory is not null || !OperatingSystem.IsWindows())
            {
                NativeUsbRuntime.EnsureAvailable();
                foreach (var identity in LibUsbTransportFactory.Enumerate(serialNumber: options.UsbSerial))
                {
                    if (registration?.DeviceIdentifier is { } identifier &&
                        !identifier.Identify(new UsbDeviceInfo { VendorId = identity.VendorId, ProductId = identity.ProductId }).IsSuccess)
                        continue;
                    if (options.UsbBus is { } bus && bus != identity.BusNumber ||
                        options.UsbPortPath is { } port && port != identity.PortPath)
                        continue;
                    _ui.WriteLine(Strings.FormatCli_UsbTopology(identity.VendorId.ToString("X4"), identity.ProductId.ToString("X4"),
                        identity.BusNumber?.ToString() ?? "?", identity.PortPath ?? "?"));
                }
                return 0;
            }
            foreach (var device in UsbEnumeratorFactory.Create().GetDevices())
                _ui.WriteLine($"{device.VendorId?.ToString("X4") ?? "????"}:{device.ProductId?.ToString("X4") ?? "????"} {device.FriendlyName ?? device.Description ?? Strings.Cli_UsbDeviceFallback}");
            return 0;
        }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
    }

    internal static string[] Tokenize(string line)
    {
        var tokens = new List<string>(); var current = new System.Text.StringBuilder(); char quote = '\0';
        foreach (char ch in line)
        {
            if (quote != '\0') { if (ch == quote) quote = '\0'; else current.Append(ch); }
            else if (ch is '\'' or '"') quote = ch;
            else if (char.IsWhiteSpace(ch)) { if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); } }
            else current.Append(ch);
        }
        if (quote != '\0') throw new ArgumentException(Strings.Cli_UnclosedQuote);
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens.ToArray();
    }

    private sealed class ActiveConnection(IProtocol protocol, ITransport transport, ProtocolRegistration registration) : IAsyncDisposable
    {
        public IProtocol Protocol { get; private set; } = protocol;
        public ProtocolRegistration Registration { get; private set; } = registration;
        private ITransport? _transport = transport;
        public async ValueTask ReleaseAsync()
        {
            if (_transport is not { } owned) return;
            _transport = null;
            try { await Protocol.DisposeAsync(); }
            finally { owned.Dispose(); }
        }
        public void Replace(IProtocol next, ITransport nextTransport, ProtocolRegistration nextRegistration)
        { Protocol = next; _transport = nextTransport; Registration = nextRegistration; }
        public ValueTask DisposeAsync() => ReleaseAsync();
    }
}

internal sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}
