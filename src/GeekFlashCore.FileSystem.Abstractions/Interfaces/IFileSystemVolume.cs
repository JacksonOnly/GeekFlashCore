namespace GeekFlashCore.FileSystem.Abstractions;

public interface IFileSystemVolume : IDisposable
{
    FileSystemVolumeInfo Info { get; }
    FileSystemEntry Root { get; }

    IFileSystemDirectoryReader OpenDirectory(FileSystemNodeId nodeId);
    Stream OpenRead(FileSystemNodeId nodeId);
    FileSystemName ReadSymbolicLink(FileSystemNodeId nodeId);
    IFileSystemXattrReader OpenExtendedAttributes(FileSystemNodeId nodeId);
    object GetNativeDetails(FileSystemNodeId nodeId);
}