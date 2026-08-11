namespace GeekFlashCore.Transport.Abstractions;

public interface ITransport : IDisposable
{
    bool IsOpen { get; }
    void Open();
    void Close();
    void Write(ReadOnlySpan<byte> data, int? timeoutInMilliseconds = null);
    void Write(byte[] data, int offset, int count, int? timeoutInMilliseconds = null);
    int Read(Span<byte> data, int? timeoutInMilliseconds = null);
    int Read(byte[] data, int offset, int count, int? timeoutInMilliseconds = null);
    int ReadExact(Span<byte> destination, int? timeoutInMilliseconds = null);
    int ReadAvailable(Span<byte> data);
    void Flush();
}