namespace GeekFlashCore.FileSystem.Abstractions;

public readonly record struct FileSystemXattrEntry(
    FileSystemXattrNamespace Namespace,
    FileSystemName Name,
    long ValueLength,
    object? NativeDetails = null);
