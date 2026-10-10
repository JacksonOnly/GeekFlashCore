using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

/// <summary>Transfers package ownership with a loader resource instead of leaving borrowed entries alive.</summary>
internal sealed class FirmwareLoaderSource : IDataSource, IDisposable
{
    private FirmwarePackageInput? _input;
    private FirmwareLoaderSource(FirmwarePackageInput input) => _input = input;
    internal static FirmwareLoaderSource Open(string path, CancellationToken ct)
    {
        var input = FirmwarePackageInput.Open(path, ct);
        if (input.Source is not null) return new(input);
        input.Dispose();
        throw new ArgumentException(Strings.Cli_SprdLoaderSourceInvalid);
    }
    private IDataSource Source => Volatile.Read(ref _input)?.Source ?? throw new ObjectDisposedException(nameof(FirmwareLoaderSource));
    public long Length => Source.Length;
    public Stream OpenStream() => Source.OpenStream();
    public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) => Source.OpenStreamAsync(ct);
    public void Dispose() => Interlocked.Exchange(ref _input, null)?.Dispose();
}
