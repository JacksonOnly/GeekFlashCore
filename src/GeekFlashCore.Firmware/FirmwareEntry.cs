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
    private readonly Func<CancellationToken, long>? _resolveLength;
    private long _length;
    internal FirmwareEntry(FirmwarePackage package, int index, string name, long length, Func<CancellationToken, Stream> factory,
        Func<CancellationToken, long>? resolveLength = null, string? resourceId = null)
    { _package = package; Index = index; Name = name; _length = length; _factory = factory; _resolveLength = resolveLength; ResourceId = resourceId; }
    /// <summary>Zero-based index in container order.</summary>
    public int Index { get; }
    /// <summary>Normalized relative file path using forward slashes.</summary>
    public string Name { get; }
    /// <summary>Container-declared resource role (PAC File-ID), or null for formats without one. Not a device identity or execution proof.</summary>
    public string? ResourceId { get; }
    /// <summary>Exact plaintext length in bytes; a deferred virtual image is parsed on first access.</summary>
    public long Length => _resolveLength is null ? _length : GetLength();
    /// <summary>Known exact length without I/O, or null until a deferred virtual image has been parsed.</summary>
    public long? KnownLength
    {
        get { long value = Volatile.Read(ref _length); return value >= 0 ? value : null; }
    }
    /// <summary>Resolves the exact length with cancellation and caches only a successful result.</summary>
    public long GetLength(CancellationToken cancellationToken = default) => _package.Measure(token =>
    {
        if (_length < 0)
        {
            long length = _resolveLength!(token);
            _package.Check(token);
            if (length < 0) throw new InvalidDataException(Localization.Strings.InvalidRange);
            Volatile.Write(ref _length, length);
        }
        return _length;
    }, cancellationToken);
    /// <summary>Opens an independent caller-owned stream.</summary>
    public Stream OpenStream() => OpenStream(default);
    /// <summary>Opens a stream whose reads and seeks observe cancellation.</summary>
    public Stream OpenStream(CancellationToken cancellationToken) => _package.Open(token =>
    {
        if (_resolveLength is not null) GetLength(token);
        return _factory(token);
    }, cancellationToken);
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
