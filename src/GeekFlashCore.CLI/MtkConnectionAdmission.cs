using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using LibUsbDotNet.LibUsb;

namespace GeekFlashCore.CLI;

// Only the narrowly scoped initial probe may create this marker. Never wrap ConnectAsync.
internal sealed class MtkInitialProbeException(Exception inner) : Exception(inner.Message, inner);
internal sealed class MtkDeviceWaitTimeoutException(int timeoutMilliseconds, Exception? inner = null)
    : TimeoutException(Strings.FormatCli_DeviceWaitTimedOut(timeoutMilliseconds), inner);

internal static class MtkConnectionAdmission
{
    internal static bool IsUsbFailure(Exception exception) =>
        exception is UsbException or TimeoutException { InnerException: UsbException };

    // The caller must release a failed attempt before it throws. This loop is for
    // connecting only: never pass an interactive command or a storage write here.
    internal static async Task<T> WaitForConnectionAsync<T>(
        Func<int?, CancellationToken, Task<T>> attempt, int? timeoutMilliseconds,
        Action reportRetry, CancellationToken ct, Func<bool>? canRetry = null)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool reported = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int? remaining = timeoutMilliseconds is { } timeout
                ? (int)Math.Max(0, timeout - elapsed.ElapsedMilliseconds) : null;
            if (remaining == 0) throw new MtkDeviceWaitTimeoutException(timeoutMilliseconds!.Value);
            try { return await attempt(remaining, ct).ConfigureAwait(false); }
            catch (Exception exception) when (IsUsbFailure(exception) && (canRetry?.Invoke() ?? true))
            {
                ct.ThrowIfCancellationRequested();
                Serilog.Log.Debug(exception, Strings.Cli_MtkConnectionUsbRetry);
                if (!reported) { reportRetry(); reported = true; }
            }
            await Task.Delay(Math.Min(250, remaining ?? 250), ct).ConfigureAwait(false);
        }
    }

    internal static async Task<IProtocol> WaitAsync(
        Func<Action, CancellationToken, Task<IProtocol>> attempt, int? timeoutMilliseconds,
        Action reportRetry, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMilliseconds is { } timeout) wait.CancelAfter(timeout);
        bool admitted = false, reported = false;
        void Accept()
        {
            wait.Token.ThrowIfCancellationRequested();
            admitted = true;
            // Loader selection and DA connection have their own budgets, not the discovery budget.
            wait.CancelAfter(Timeout.Infinite);
        }
        try
        {
            while (true)
            {
                wait.Token.ThrowIfCancellationRequested();
                try { return await attempt(Accept, wait.Token).ConfigureAwait(false); }
                catch (MtkInitialProbeException exception) when (!admitted)
                {
                    wait.Token.ThrowIfCancellationRequested();
                    Serilog.Log.Debug(exception.InnerException, Strings.Cli_MtkInitialProbeRetry);
                    if (!reported) { reportRetry(); reported = true; }
                }
                await Task.Delay(100, wait.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !admitted && wait.IsCancellationRequested)
        { throw new MtkDeviceWaitTimeoutException(timeoutMilliseconds!.Value); }
    }
}
