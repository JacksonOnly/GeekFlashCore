using System.Globalization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.UsbWatcher;
using Serilog;

namespace GeekFlashCore.CLI;

internal sealed class CliApplication
{
    private readonly ConsoleUi _ui = new();
    private readonly TransportResolver _transportResolver;

    public CliApplication() => _transportResolver = new TransportResolver();

    public async Task<int> RunAsync(CliOptions options, CancellationToken ct)
    {
        _ui.WriteBanner();
        if (options.Command == "help") { CommandLine.PrintHelp(); return 0; }
        if (options.Command.Equals("devices", StringComparison.OrdinalIgnoreCase)) return ListDevices();
        if (!ProtocolRegistry.TryResolve(options.Protocol, out var registration) || registration.Type != ProtocolType.QualcommEdl)
            throw new ArgumentException($"协议 '{options.Protocol}' 当前未注册；可用协议：{ProtocolRegistry.SupportedNames}");

        if (options.Command.Equals("interactive", StringComparison.OrdinalIgnoreCase)) return await InteractiveAsync(options, ct).ConfigureAwait(false);
        var connection = await CreateQcomAsync(options, ct).ConfigureAwait(false);
        await using var protocol = connection.Protocol;
        ITransport transport = connection.Transport;
        try
        {
            var progress = new Progress<ProgressRecord>(_ui.Report);
            if (options.Command.Equals("qcom", StringComparison.OrdinalIgnoreCase) && options.Arguments.FirstOrDefault()?.Equals("probe-sahara", StringComparison.OrdinalIgnoreCase) == true)
            {
                var target = protocol.ProbeSahara(progress);
                Console.WriteLine($"Sahara v{target.Version}, mode={target.Mode}, packet={target.MaximumPacketSizeSupported}");
                return 0;
            }
            if (options.Command.Equals("qcom", StringComparison.OrdinalIgnoreCase) && options.Arguments.FirstOrDefault()?.Equals("configure", StringComparison.OrdinalIgnoreCase) == true)
            {
                var result = protocol.ConfigureFirehose(progress);
                Console.WriteLine($"Firehose configured: {result.Status}, bytes={result.BytesTransferred}");
                return result.IsSuccess ? 0 : 1;
            }
            await protocol.ConnectAsync(progress, ct).ConfigureAwait(false);
            return await ExecuteConnectedAsync(protocol, options, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Console.WriteLine("操作已取消。"); return 130; }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
        finally { transport.Dispose(); }
    }

    private async Task<int> InteractiveAsync(CliOptions options, CancellationToken ct)
    {
        var connection = await CreateQcomAsync(options, ct).ConfigureAwait(false);
        await using var protocol = connection.Protocol;
        ITransport transport = connection.Transport;
        try
        {
            await protocol.ConnectAsync(new Progress<ProgressRecord>(_ui.Report), ct).ConfigureAwait(false);
            Console.WriteLine("已联机。通用功能: info, partitions, read, write, erase, reboot；Qualcomm 功能: qcom probe-sahara/configure/xml。");
            while (!ct.IsCancellationRequested)
            {
                Console.Write("geekflash> ");
                string line = Console.ReadLine() ?? "exit";
                if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) || line.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
                if (line.Equals("help", StringComparison.OrdinalIgnoreCase)) { CommandLine.PrintHelp(); continue; }
                try
                {
                    var parsed = CommandLine.Parse(Tokenize(line));
                    if (parsed.Command.Equals("interactive", StringComparison.OrdinalIgnoreCase)) continue;
                    await ExecuteConnectedAsync(protocol, parsed, new Progress<ProgressRecord>(_ui.Report), ct).ConfigureAwait(false);
                }
                catch (Exception exception) { _ui.LogException(exception); }
            }
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
        finally { transport.Dispose(); }
    }

    private async Task<int> ExecuteConnectedAsync(IQcomProtocol protocol, CliOptions options, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        string command = options.Command.ToLowerInvariant();
        switch (command)
        {
            case "connect": Console.WriteLine("连接成功。"); return 0;
            case "info": _ui.PrintTargetInfo(protocol.TargetInfo); return 0;
            case "partitions":
                foreach (var item in await protocol.GetPartitionsAsync(progress, ct).ConfigureAwait(false)) Console.WriteLine($"{item.Name,-32} offset={item.Offset} length={item.Length} address={item.Address}");
                return 0;
            case "read": await ReadAsync(protocol, options.Arguments, progress, ct).ConfigureAwait(false); return 0;
            case "write": await WriteAsync(protocol, options.Arguments, progress, ct).ConfigureAwait(false); return 0;
            case "erase": await EraseAsync(protocol, options.Arguments, progress, ct).ConfigureAwait(false); return 0;
            case "reboot":
                var mode = Enum.Parse<ProtocolRebootMode>(options.Arguments.FirstOrDefault() ?? "system", true);
                await protocol.RebootAsync(mode, progress, ct).ConfigureAwait(false); return 0;
            case "qcom": return ExecuteQcomCommand(protocol, options.Arguments);
            default: throw new ArgumentException($"未知命令 '{options.Command}'");
        }
    }

    private int ExecuteQcomCommand(IQcomProtocol protocol, string[] args)
    {
        switch (args.FirstOrDefault()?.ToLowerInvariant())
        {
            case "configure":
                var result = protocol.ConfigureFirehose(new Progress<ProgressRecord>(_ui.Report));
                Console.WriteLine($"Firehose configured: {result.Status}, bytes={result.BytesTransferred}"); return 0;
            case "xml":
                string path = args.Skip(1).FirstOrDefault() ?? _ui.Ask("XML 文件路径");
                string xml = File.ReadAllText(path);
                var response = protocol.ExecuteFirehoseXml(xml);
                Console.WriteLine($"XML result: {response.Status}"); return response.IsSuccess ? 0 : 1;
            default: throw new ArgumentException("qcom 子命令必须是 probe-sahara、configure 或 xml");
        }
    }

    private static async Task ReadAsync(IQcomProtocol protocol, string[] args, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        if (args.Length < 2) throw new ArgumentException("read 用法: read <target> <output>");
        string output = args[1];
        await using var stream = File.Create(output);
        var destination = new ReadDestination { Target = ParseTarget(args[0]), OutputStream = stream, OwnsStream = false };
        await protocol.ReadAsync(destination, progress, ct).ConfigureAwait(false);
    }

    private static async Task WriteAsync(IQcomProtocol protocol, string[] args, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        if (args.Length < 2) throw new ArgumentException("write 用法: write <source> <target>");
        var source = new FileDataSource(args[0]);
        await protocol.WriteAsync(new WriteSource { Source = source, Target = ParseTarget(args[1]) }, progress, ct).ConfigureAwait(false);
    }

    private static async Task EraseAsync(IQcomProtocol protocol, string[] args, IProgress<ProgressRecord> progress, CancellationToken ct)
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

    private int ListDevices()
    {
        try
        {
            foreach (var device in UsbEnumeratorFactory.Create().GetDevices())
                Console.WriteLine($"{device.VendorId?.ToString("X4") ?? "????"}:{device.ProductId?.ToString("X4") ?? "????"} {device.FriendlyName ?? device.Description ?? "USB device"}");
            return 0;
        }
        catch (Exception exception) { _ui.LogException(exception); return 1; }
    }

    private async Task<(QcomProtocol Protocol, ITransport Transport)> CreateQcomAsync(CliOptions options, CancellationToken ct)
    {
        ITransport transport = await _transportResolver.ResolveAsync(options, ct).ConfigureAwait(false);
        OplusDigestMode mode = options.OplusMode;
        if (!string.IsNullOrWhiteSpace(options.OplusDigest) && mode == OplusDigestMode.None) mode = OplusDigestMode.OplusDigestPt;
        var qcomOptions = new QcomProtocolOptions
        {
            VendorOverride = options.Vendor,
            ReadTimeoutMilliseconds = options.ReadTimeout,
            WriteTimeoutMilliseconds = options.WriteTimeout,
            OplusDigest = new OplusDigestConfiguration { Mode = mode },
            FirehoseDigest = new FirehoseDigestConfiguration { Enabled = !string.IsNullOrWhiteSpace(options.Digest) },
            FirehoseVip = new FirehoseVipConfiguration { Enabled = !string.IsNullOrWhiteSpace(options.VipSigned) }
        };
        var protocol = new QcomProtocol(transport, qcomOptions,
            new ConsoleSaharaImageProvider(_ui, options.Loader),
            mode == OplusDigestMode.None ? null : new ConsoleOplusDigestProvider(_ui, options.OplusDigest),
            new ConsoleAuthenticationProvider(_ui), null, leaveTransportOpen: true,
            firehoseDigestProvider: qcomOptions.FirehoseDigest.Enabled ? new ConsoleFirehoseDigestProvider(_ui, options.Digest) : null,
            firehoseVipProvider: qcomOptions.FirehoseVip.Enabled ? new ConsoleVipProvider(_ui, options.VipSigned, options.VipChained) : null);
        return (protocol, transport);
    }

    private static string[] Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (char ch in line)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0'; else current.Append(ch);
            }
            else if (ch is '\'' or '"') quote = ch;
            else if (char.IsWhiteSpace(ch)) { if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); } }
            else current.Append(ch);
        }
        if (quote != '\0') throw new ArgumentException("命令包含未闭合引号");
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens.ToArray();
    }
}
