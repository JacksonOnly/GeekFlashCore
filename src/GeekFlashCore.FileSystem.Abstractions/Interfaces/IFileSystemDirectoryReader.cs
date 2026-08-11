namespace GeekFlashCore.FileSystem.Abstractions.Interfaces;

public interface IFileSystemDirectoryReader : IDisposable
{
    FileSystemEntry Current { get; }
    bool MoveNext();
}