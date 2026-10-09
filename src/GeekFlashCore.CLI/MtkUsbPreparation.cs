namespace GeekFlashCore.CLI;

internal sealed class MtkUsbPreparation(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long? _lastSuccess;
    public async Task EnsureAsync(Func<CancellationToken, Task> prepare, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_lastSuccess is { } last && _time.GetElapsedTime(last) < TimeSpan.FromSeconds(1)) return;
        await prepare(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _lastSuccess = _time.GetTimestamp();
    }
}
