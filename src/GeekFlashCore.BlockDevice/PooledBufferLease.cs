using System.Buffers;

namespace GeekFlashCore.BlockDevice;

public sealed class PooledBufferLease : IMemoryOwner<byte>, IAsyncDisposable
{
    private readonly ArrayPool<byte> _pool;
    private readonly int _requestedLength;
    private ByteBudgetLease? _reservation;
    private byte[]? _array;

    internal PooledBufferLease(
        ArrayPool<byte> pool,
        byte[] array,
        int requestedLength,
        ByteBudgetLease reservation)
    {
        _pool = pool;
        _array = array;
        _requestedLength = requestedLength;
        _reservation = reservation;
        RentedLength = array.Length;
    }

    public Memory<byte> Memory
    {
        get
        {
            byte[] array = Volatile.Read(ref _array) ??
                throw new ObjectDisposedException(nameof(PooledBufferLease));
            return array.AsMemory(0, _requestedLength);
        }
    }

    public int RentedLength { get; }

    public void Dispose()
    {
        byte[]? array = Interlocked.Exchange(ref _array, null);
        if (array is null)
        {
            return;
        }

        ByteBudgetLease? reservation = Interlocked.Exchange(ref _reservation, null);
        try
        {
            array.AsSpan(0, _requestedLength).Clear();
            _pool.Return(array);
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
