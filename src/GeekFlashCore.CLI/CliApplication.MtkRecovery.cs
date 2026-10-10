using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk;

namespace GeekFlashCore.CLI;

internal sealed partial class CliApplication
{
    private async Task<ActiveConnection> WaitForInteractiveConnectionAsync(CliOptions options,
        ProtocolRegistration? requested, CancellationToken ct)
    {
        bool retryMtk = IsMtkConnection(options) || requested?.Type == ProtocolType.Mtk;
        async Task<ActiveConnection> Attempt(int? remaining, CancellationToken token)
        {
            ActiveConnection? active = null;
            var attemptOptions = remaining is { } budget
                ? options with { DeviceWaitTimeout = budget, HasExplicitDeviceWaitTimeout = true } : options;
            try
            {
                var connection = await CreateConnectionAsync(attemptOptions, requested, token).ConfigureAwait(false);
                active = new(connection.Protocol, connection.Transport, connection.Registration);
                retryMtk |= active.Protocol.Type == ProtocolType.Mtk;
                _ui.WriteLine(Strings.Cli_Connecting);
                if (!active.Protocol.IsConnected)
                {
                    await active.Protocol.ConnectAsync(_progress, token).ConfigureAwait(false);
                    MtkProtocolHostAdapter.InitializeExtension(active.Protocol, options, token);
                }
                return active;
            }
            catch
            {
                if (active is not null)
                {
                    try { await active.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) when (active.Protocol.Type == ProtocolType.Mtk)
                    { Serilog.Log.Debug(cleanup, Strings.Cli_MtkConnectionUsbRetry); }
                }
                throw;
            }
        }
        return await MtkConnectionAdmission.WaitForConnectionAsync(Attempt,
            retryMtk && options.HasExplicitDeviceWaitTimeout ? options.DeviceWaitTimeout : null,
            () => { _cleanReconnectBoundary = false; _ui.StopMtkProgress(); _ui.WriteLine(Strings.Cli_MtkConnectionUsbRetry); }, ct,
            () => retryMtk).ConfigureAwait(false);
    }

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
