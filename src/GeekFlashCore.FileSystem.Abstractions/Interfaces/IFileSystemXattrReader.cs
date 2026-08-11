namespace GeekFlashCore.FileSystem.Abstractions.Interfaces;

public interface IFileSystemXattrReader : IDisposable
{
    FileSystemXattrEntry Current { get; }
    bool MoveNext();
    Stream OpenValue();
}