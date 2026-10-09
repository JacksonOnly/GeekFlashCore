using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk;

namespace GeekFlashCore.CLI;

internal sealed partial class CliApplication
{
    private static bool IsMtkConnection(CliOptions options) =>
        options.Protocol is not null && ProtocolRegistry.TryResolve(options.Protocol, out var registration) && registration.Type == ProtocolType.Mtk ||
        options.Protocol is null && options.Usb is { } usb && TransportResolver.TryParseUsb(usb, out int vid, out int pid) &&
            MtkDeviceIdentify.IsSupported((ushort)vid, (ushort)pid);

    internal async Task<CliOptions?> WaitForMtkRetryAsync(CliOptions options, CancellationToken ct)
    {
        _ui.WriteLine(Strings.Cli_MtkOffline);
        while (true)
        {
            string line = await _ui.ReadCommandAsync("geekflash[offline]> ",
                prefix => new[] { "reconnect", "help", "help mtk", "devices", "exit" }.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray(), ct).ConfigureAwait(false) ?? "exit";
            if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) || line.Equals("quit", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var parsed = CommandLine.ParseSession(Tokenize(line), options with { Protocol = "mtk" });
                switch (parsed.Command)
                {
                    case "interactive": continue;
                    case "help": CommandLine.PrintRequestedHelp(parsed.Arguments.Length == 0 ? ["mtk"] : parsed.Arguments, _ui); continue;
                    case "devices": ListDevices(parsed, MtkProtocolHostAdapter.Registration); continue;
                    case "connect": parsed = parsed with { Command = "reconnect" }; goto case "reconnect";
                    case "reconnect": CommandSyntax.Validate(parsed); return parsed;
                    default: _ui.WriteLine(Strings.Cli_ReconnectRequired); break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { _ui.LogException(exception); }
        }
    }
}
