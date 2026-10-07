using System.Buffers;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Firmware;

/// <summary>A firmware file exposed as a stable, reopenable IDataSource.</summary>
/// <remarks>Each stream is readable and seekable, starts at zero, and is owned by its caller.
/// Compressed backward seeks replay decompression; no image or temporary file is materialized.</remarks>
public sealed class FirmwareEntry : IDataSource
{
    private readonly FirmwarePackage _package;
    private readonly Func<CancellationToken, Stream> _factory;
    internal FirmwareEntry(FirmwarePackage package, int index, string name, long length, Func<CancellationToken, Stream> factory)
    { _package = package; Index = index; Name = name; Length = length; _factory = factory; }
    /// <summary>Zero-based index in container order.</summary>
    public int Index { get; }
    /// <summary>Normalized relative file path using forward slashes.</summary>
    public string Name { get; }
    /// <summary>Exact plaintext length in bytes.</summary>
    public long Length { get; }
    /// <summary>Opens an independent caller-owned stream.</summary>
    public Stream OpenStream() => OpenStream(default);
    /// <summary>Opens a stream whose reads and seeks observe cancellation.</summary>
    public Stream OpenStream(CancellationToken cancellationToken) => _package.Open(_factory, cancellationToken);
    /// <summary>Opens the synchronous offline source without waiting on asynchronous resources.</summary>
    public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) => ValueTask.FromResult(OpenStream(ct));
    /// <summary>Copies plaintext to a borrowed writable stream with bounded memory.</summary>
    public long CopyTo(Stream destination, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var input = OpenStream(cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_package.Options.BufferSize);
        try
        {
            long copied = 0; int read;
            while ((read = input.Read(buffer, 0, _package.Options.BufferSize)) != 0)
            { cancellationToken.ThrowIfCancellationRequested(); destination.Write(buffer, 0, read); copied = checked(copied + read); progress?.Report(copied); }
            return copied;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
