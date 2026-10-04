using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.UsbWatcher;

namespace GeekFlashCore.CLI;

internal sealed class CliApplication
{
    private readonly ConsoleUi _ui;
    private readonly TransportResolver _transportResolver = new();
    private readonly IProgress<ProgressRecord> _progress;

    public CliApplication(ConsoleUi? ui = null)
    {
        _ui = ui ?? new ConsoleUi();
        _progress = new ImmediateProgress<ProgressRecord>(_ui.Report);
    }

    public async Task<int> RunAsync(CliOptions options, CancellationToken ct)
    {
        options.Validate();
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
        if (options.Command == "help") { CommandLine.PrintHelp(); return 0; }
        if (options.Command.Equals("devices", StringComparison.OrdinalIgnoreCase)) return ListDevices();
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
            await protocol.ConnectAsync(progress, ct).ConfigureAwait(false);
            return await ExecuteCommandAsync(protocol, connection.Registration, options, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { _ui.ShowCancelled(); return 130; }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
    }

    private async Task<int> InteractiveAsync(CliOptions options, CancellationToken ct)
    {
        var connection = await CreateConnectionAsync(options, null, ct).ConfigureAwait(false);
        using ITransport transport = connection.Transport;
        await using var protocol = connection.Protocol;
        try
        {
            _ui.WriteLine(Strings.Cli_Connecting);
            await protocol.ConnectAsync(_progress, ct).ConfigureAwait(false);
            _ui.WriteLine(Strings.FormatCli_ConnectedHelp(connection.Registration.DisplayName));
            while (!ct.IsCancellationRequested)
            {
                _ui.Write("geekflash> ");
                string line = await _ui.ReadInputAsync(ct).ConfigureAwait(false) ?? "exit";
                if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) || line.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
                try
                {
                    var parsed = CommandLine.Parse(Tokenize(line));
                    if (parsed.Command.Equals("interactive", StringComparison.OrdinalIgnoreCase)) continue;
                    await ExecuteCommandAsync(protocol, connection.Registration, parsed, _progress, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { _ui.ShowCancelled(); }
                catch (Exception exception) { _ui.LogException(exception); }
            }
            return 0;
        }
        catch (OperationCanceledException) { _ui.ShowCancelled(); return 130; }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
    }

    private async Task<int> ExecuteCommandAsync(IProtocol protocol, ProtocolRegistration registration, CliOptions options, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        try { options = NormalizeAndValidate(options, registration); }
        catch (CommandUsageException exception) { _ui.WriteLine(exception.Message); return 2; }
        ct.ThrowIfCancellationRequested();
        registration.CommandSet?.ValidateAvailability(protocol, options.Command);
        if (registration.CommandSet is { } commands && commands.Handles(options.Command))
            return await commands.ExecuteAsync(protocol, options, _ui, progress, ct);
        IProtocolCommandHandler? special = FindSpecialHandler(registration, options);
        if (options.Command.Equals(special?.Name, StringComparison.OrdinalIgnoreCase) && special is not null)
            return await special.ExecuteAsync(protocol, options.Arguments, _ui, progress, ct).ConfigureAwait(false);

        switch (options.Command.ToLowerInvariant())
        {
            case "connect":
                _ui.WriteLine(Strings.Cli_Connecting);
                await protocol.ConnectAsync(progress, ct).ConfigureAwait(false);
                _ui.WriteLine(Strings.FormatCli_ConnectedHelp(registration.DisplayName));
                return 0;
            case "info":
                registration.InfoPresenter?.Invoke(protocol, _ui);
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
            case "help": CommandLine.PrintHelp(); registration.CommandSet?.PrintHelp(protocol, _ui); return 0;
            default: throw new ArgumentException(Strings.FormatCli_UnknownCommand(options.Command));
        }
    }

    private async Task<(IProtocol Protocol, ITransport Transport, ProtocolRegistration Registration)> CreateConnectionAsync(CliOptions options, ProtocolRegistration? requested, CancellationToken ct)
    {
        TransportResolution resolution = await _transportResolver.ResolveAsync(options, ct, requested).ConfigureAwait(false);
        ProtocolRegistration registration = requested ?? resolution.Registration;
        try { return (registration.Factory(new ProtocolHostContext(_ui, options), resolution.Transport), resolution.Transport, registration); }
        catch { resolution.Transport.Dispose(); throw; }
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

    private int ListDevices()
    {
        try
        {
            foreach (var device in UsbEnumeratorFactory.Create().GetDevices())
                _ui.WriteLine($"{device.VendorId?.ToString("X4") ?? "????"}:{device.ProductId?.ToString("X4") ?? "????"} {device.FriendlyName ?? device.Description ?? Strings.Cli_UsbDeviceFallback}");
            return 0;
        }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
    }

    private static string[] Tokenize(string line)
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
}

internal sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}
