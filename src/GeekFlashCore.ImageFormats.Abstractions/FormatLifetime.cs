using System.Threading;

namespace GeekFlashCore.ImageFormats.Abstractions;

public sealed class FormatLifetime : IDisposable
{
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void ThrowIfDisposed(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ObjectDisposedException.ThrowIf(IsDisposed, instance);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}
