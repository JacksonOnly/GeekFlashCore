using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.CLI;

// Only the narrowly scoped initial probe may create this marker. Never wrap ConnectAsync.
internal sealed class MtkInitialProbeException(Exception inner) : Exception(inner.Message, inner);
internal sealed class MtkDeviceWaitTimeoutException(int timeoutMilliseconds, Exception? inner = null)
    : TimeoutException(Strings.FormatCli_DeviceWaitTimedOut(timeoutMilliseconds), inner);

internal static class MtkConnectionAdmission
{
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
