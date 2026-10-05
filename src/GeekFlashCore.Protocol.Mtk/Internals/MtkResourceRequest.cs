namespace GeekFlashCore.Protocol.Mtk.Internals;

internal static class MtkResourceRequest
{
    public static async ValueTask<T> Get<T>(Func<CancellationToken, ValueTask<T>> request, int timeout, CancellationToken token)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(timeout);
        Task<T> task;
        try
        {
            task = request(cancellation.Token).AsTask();
        }
        catch { cancellation.Dispose(); throw; }
        try
        {
            return await task.WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = ReleaseLate(task, cancellation);
            cancellation = null!;
            if (token.IsCancellationRequested)
                throw;
            throw new TimeoutException(Strings.Timeout);
        }
        finally { cancellation?.Dispose(); }
    }
    private static async Task ReleaseLate<T>(Task<T> task, CancellationTokenSource cancellation)
    {
        try
        {
            T value = await task.ConfigureAwait(false);
            if (value is IDisposable owned)
                owned.Dispose();
        }
        catch { /* Exception observed; no second log without session context. */ }
        finally { cancellation.Dispose(); }
    }
}
