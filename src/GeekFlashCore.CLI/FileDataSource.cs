using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed class FileDataSource(string path) : IDataSource
{
    public string Path { get; } = System.IO.Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    public long Length => new FileInfo(Path).Length;
    public Stream OpenStream() => File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
    public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(OpenStream());
    }
}
