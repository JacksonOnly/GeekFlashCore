namespace GeekFlashCore.FileSystem.Abstractions;

public interface IFileSystemXattrReader : IDisposable
{
    FileSystemXattrEntry Current { get; }
    bool MoveNext();
    Stream OpenValue();
}