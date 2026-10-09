using System.Diagnostics;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal sealed partial class ConsoleUi
{
    private int _waitColumns;
    private string? _lastStaticWaitMessage;
    internal IAsyncDisposable BeginDeviceWait(string message, Action cancel) => new DeviceWait(this, message, cancel);
    private void RenderDeviceWait(string message, TimeSpan elapsed, int frame)
    {
        lock (_gate)
        {
            ClearProgressUnsafe();
            string text = Strings.FormatCli_DeviceWaitStatus("|/-\\"[frame % 4], message, elapsed.ToString(@"hh\:mm\:ss"));
            int width;
            try { width = Math.Max(1, Console.WindowWidth - 1); } catch (IOException) { width = 79; }
            while (text.Length > 0 && ProgressDisplay.Cells(text) > width) text = text[..^1];
            Console.Write(text); _waitColumns = ProgressDisplay.Cells(text);
        }
    }
    private void ClearDeviceWaitUnsafe()
    {
        if (_waitColumns == 0) return;
        Console.Write("\r" + new string(' ', _waitColumns) + "\r"); _waitColumns = 0;
    }
    private sealed class DeviceWait : IAsyncDisposable
    {
        private readonly ConsoleUi _ui;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private Task? _disposing;
        internal DeviceWait(ConsoleUi ui, string message, Action cancel)
        {
            _ui = ui;
            if (!ui.AllowPrompts || !ui._input.CanPollKeys)
            {
                lock (ui._gate)
                {
                    if (ui._lastStaticWaitMessage != message)
                    { ui.WriteLine(message); ui._lastStaticWaitMessage = message; }
                }
                _worker = Task.CompletedTask; return;
            }
            _worker = Task.WhenAll(Task.Run(async () =>
            {
                var elapsed = Stopwatch.StartNew();
                for (int frame = 0; ; frame++)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    ui.RenderDeviceWait(message, elapsed.Elapsed, frame);
                    await Task.Delay(125, _stop.Token).ConfigureAwait(false);
                }
            }), ui._input.WaitForEscapeAsync(cancel, _stop.Token));
        }
        public ValueTask DisposeAsync()
        { lock (this) return new(_disposing ??= StopAsync()); }
        private async Task StopAsync()
        {
            _stop.Cancel();
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            { Serilog.Log.Debug(exception, Strings.Cli_OperationCancelled); }
            finally
            {
                try { lock (_ui._gate) _ui.ClearDeviceWaitUnsafe(); }
                catch (IOException exception) { Serilog.Log.Debug(exception, Strings.Cli_OperationCancelled); }
                finally { _stop.Dispose(); }
            }
        }
    }
}
