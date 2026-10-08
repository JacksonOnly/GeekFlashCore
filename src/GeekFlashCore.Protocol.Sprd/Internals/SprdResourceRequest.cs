namespace GeekFlashCore.Protocol.Sprd.Internals;

internal static class SprdResourceRequest
{
    internal static async Task<SprdConnectionResources> Get(ISprdLoaderProvider provider, SprdBootStage entry,
        int timeout, CancellationToken token)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(timeout);
        Task<SprdConnectionResources> task;
        try { task = provider.GetLoadersAsync(entry, cancellation.Token).AsTask(); }
        catch { cancellation.Dispose(); throw; }
        try { return await task.WaitAsync(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = ReleaseLate(task, cancellation); cancellation = null!;
            token.ThrowIfCancellationRequested();
            throw new TimeoutException(Strings.Timeout);
        }
        finally { cancellation?.Dispose(); }
    }
    private static async Task ReleaseLate(Task<SprdConnectionResources> task, CancellationTokenSource cancellation)
    {
        try { (await task.ConfigureAwait(false))?.Dispose(); }
        catch { /* Observe late provider/disposal faults without leaking resource data into logs. */ }
        finally { cancellation.Dispose(); }
    }
}
