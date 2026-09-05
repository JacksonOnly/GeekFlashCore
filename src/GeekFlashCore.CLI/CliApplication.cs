using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.UsbWatcher;
using Serilog;

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
        _ui.WriteBanner();
        if (options.Command == "help") { CommandLine.PrintHelp(); return 0; }
        if (options.Command.Equals("devices", StringComparison.OrdinalIgnoreCase)) return ListDevices();

        ProtocolRegistration? requestedRegistration = null;
        if (!string.IsNullOrWhiteSpace(options.Protocol) && !ProtocolRegistry.TryResolve(options.Protocol, out requestedRegistration))
            throw new ArgumentException($"协议 '{options.Protocol}' 当前未注册；可用协议：{ProtocolRegistry.SupportedNames}");
        if (requestedRegistration is null && !options.Command.Equals("interactive", StringComparison.OrdinalIgnoreCase))
            ProtocolRegistry.TryResolveCommand(options.Command, out requestedRegistration);
        if (options.Command.Equals("interactive", StringComparison.OrdinalIgnoreCase))
            return await InteractiveAsync(options, ct).ConfigureAwait(false);

        var connection = await CreateConnectionAsync(options, requestedRegistration, ct).ConfigureAwait(false);
        await using var protocol = connection.Protocol;
        ITransport transport = connection.Transport;
        try
        {
            var progress = _progress;
            IProtocolCommandHandler? special = FindSpecialHandler(connection.Registration, options.Arguments);
            if (special is not null && !special.RequiresConnection(options.Arguments))
                return await special.ExecuteAsync(protocol, options.Arguments, _ui, progress, ct).ConfigureAwait(false);

            await protocol.ConnectAsync(progress, ct).ConfigureAwait(false);
            return await ExecuteCommandAsync(protocol, connection.Registration, options, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { _ui.WriteLine("操作已取消。"); return 130; }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
        finally { transport.Dispose(); }
    }

    private async Task<int> InteractiveAsync(CliOptions options, CancellationToken ct)
    {
        var connection = await CreateConnectionAsync(options, null, ct).ConfigureAwait(false);
        await using var protocol = connection.Protocol;
        ITransport transport = connection.Transport;
        try
        {
            await protocol.ConnectAsync(_progress, ct).ConfigureAwait(false);
            _ui.WriteLine($"已联机到 {connection.Registration.DisplayName}。通用功能: info, partitions, read, write, erase, reboot。协议功能: {string.Join("; ", connection.Registration.CommandHandlers.Select(handler => handler.HelpText))}");
            while (!ct.IsCancellationRequested)
            {
                _ui.Write("geekflash> ");
                string line = Console.ReadLine() ?? "exit";
                if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) || line.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
                if (line.Equals("help", StringComparison.OrdinalIgnoreCase)) { CommandLine.PrintHelp(); continue; }
                try
                {
                    var parsed = CommandLine.Parse(Tokenize(line));
                    if (parsed.Command.Equals("interactive", StringComparison.OrdinalIgnoreCase)) continue;
                    await ExecuteCommandAsync(protocol, connection.Registration, parsed, _progress, ct).ConfigureAwait(false);
                }
                catch (Exception exception) { _ui.LogException(exception); }
            }
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
        finally { transport.Dispose(); }
    }

    private async Task<int> ExecuteCommandAsync(IProtocol protocol, ProtocolRegistration registration, CliOptions options, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        IProtocolCommandHandler? special = FindSpecialHandler(registration, options.Arguments);
        if (options.Command.Equals(special?.Name, StringComparison.OrdinalIgnoreCase) && special is not null)
            return await special.ExecuteAsync(protocol, options.Arguments, _ui, progress, ct).ConfigureAwait(false);

        switch (options.Command.ToLowerInvariant())
        {
            case "connect": _ui.WriteLine($"已连接: {registration.DisplayName}"); return 0;
            case "info":
                registration.InfoPresenter?.Invoke(protocol, _ui);
                return 0;
            case "partitions":
                foreach (var item in await protocol.GetPartitionsAsync(progress, ct).ConfigureAwait(false)) _ui.WriteLine($"{item.Name,-32} offset={item.Offset} length={item.Length} address={item.Address}");
                return 0;
            case "read": await ReadAsync(protocol, options.Arguments, progress, ct).ConfigureAwait(false); return 0;
            case "write": await WriteAsync(protocol, options.Arguments, progress, ct).ConfigureAwait(false); return 0;
            case "erase": await EraseAsync(protocol, options.Arguments, progress, ct).ConfigureAwait(false); return 0;
            case "reboot":
                var mode = Enum.Parse<ProtocolRebootMode>(options.Arguments.FirstOrDefault() ?? "system", true);
                await protocol.RebootAsync(mode, progress, ct).ConfigureAwait(false); return 0;
            default: throw new ArgumentException($"未知命令 '{options.Command}'");
        }
    }

    private async Task<(IProtocol Protocol, ITransport Transport, ProtocolRegistration Registration)> CreateConnectionAsync(CliOptions options, ProtocolRegistration? requested, CancellationToken ct)
    {
        TransportResolution resolution = await _transportResolver.ResolveAsync(options, ct, requested).ConfigureAwait(false);
        ProtocolRegistration registration = requested ?? resolution.Registration;
        return (registration.Factory(new ProtocolHostContext(_ui, options), resolution.Transport), resolution.Transport, registration);
    }

    private static IProtocolCommandHandler? FindSpecialHandler(ProtocolRegistration registration, IReadOnlyList<string> arguments) => registration.CommandHandlers.FirstOrDefault(handler => handler.Handles(arguments));

    private int ListDevices()
    {
        try
        {
            foreach (var device in UsbEnumeratorFactory.Create().GetDevices())
                _ui.WriteLine($"{device.VendorId?.ToString("X4") ?? "????"}:{device.ProductId?.ToString("X4") ?? "????"} {device.FriendlyName ?? device.Description ?? "USB device"}");
            return 0;
        }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
    }

    private static async Task ReadAsync(IProtocol protocol, string[] args, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        if (args.Length < 2) throw new ArgumentException("read 用法: read <target> <output>");
        await using var stream = File.Create(args[1]);
        await protocol.ReadAsync(new ReadDestination { Target = ParseTarget(args[0]), OutputStream = stream, OwnsStream = false }, progress, ct).ConfigureAwait(false);
    }

    private static async Task WriteAsync(IProtocol protocol, string[] args, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        if (args.Length < 2) throw new ArgumentException("write 用法: write <source> <target>");
        await protocol.WriteAsync(new WriteSource { Source = new FileDataSource(args[0]), Target = ParseTarget(args[1]) }, progress, ct).ConfigureAwait(false);
    }

    private static async Task EraseAsync(IProtocol protocol, string[] args, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        if (args.Length < 1) throw new ArgumentException("erase 用法: erase <target>");
        await protocol.EraseAsync(ParseTarget(args[0]), progress, ct).ConfigureAwait(false);
    }

    private static StorageTarget ParseTarget(string value)
    {
        if (value.StartsWith("partition:", StringComparison.OrdinalIgnoreCase)) return new PartitionTarget { Name = value[10..] };
        string[] parts = value.Split(':');
        if (parts.Length == 3 && parts[0].Equals("sector", StringComparison.OrdinalIgnoreCase)) return new SectorTarget { StartSector = long.Parse(parts[1]), SectorCount = long.Parse(parts[2]) };
        if (parts.Length == 3 && parts[0].Equals("offset", StringComparison.OrdinalIgnoreCase)) return new OffsetTarget { StartOffset = long.Parse(parts[1]), Length = long.Parse(parts[2]) };
        throw new ArgumentException("target 必须是 partition:name、sector:start:count 或 offset:start:length");
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
        if (quote != '\0') throw new ArgumentException("命令包含未闭合引号");
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens.ToArray();
    }
}

internal sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}
