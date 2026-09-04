using System.Security.Cryptography;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed class SensitiveDataOwner : IDisposable
{
    private byte[]? _buffer;

    private SensitiveDataOwner(byte[] buffer)
    {
        _buffer = buffer;
    }

    ~SensitiveDataOwner()
    {
        ClearBuffer();
    }

    public bool IsDisposed => _buffer is null;

    public int Length => GetBuffer().Length;

    public ReadOnlyMemory<byte> Memory => GetBuffer();

    public static SensitiveDataOwner CopyFrom(ReadOnlySpan<byte> source) =>
        new(source.ToArray());

    public void Dispose()
    {
        ClearBuffer();
        GC.SuppressFinalize(this);
    }

    private void ClearBuffer()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is null)
            return;
        CryptographicOperations.ZeroMemory(buffer);
    }

    private byte[] GetBuffer() =>
        Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(SensitiveDataOwner));
}
