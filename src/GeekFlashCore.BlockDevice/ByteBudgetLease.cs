namespace GeekFlashCore.BlockDevice;

public sealed class ByteBudgetLease : IDisposable, IAsyncDisposable
{
    private ByteBudget? _budget;
    private int _byteCount;

    internal ByteBudgetLease(ByteBudget budget, int byteCount)
    {
        _budget = budget;
        _byteCount = byteCount;
    }

    public int ByteCount => Volatile.Read(ref _byteCount);

    public void Dispose()
    {
        ByteBudget? budget = Interlocked.Exchange(ref _budget, null);
        if (budget is not null)
        {
            budget.Release(Volatile.Read(ref _byteCount));
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void ReduceTo(int byteCount)
    {
        int current = Volatile.Read(ref _byteCount);
        if (byteCount < 1 || byteCount > current)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        Volatile.Write(ref _byteCount, byteCount);
        _budget?.Release(current - byteCount);
    }
}
