using GeekFlashCore.FileSystem.Abstractions;

namespace GeekFlashCore.FileSystem.Erofs;

public class ErofsFileSystemException : FileSystemException
{
    public ErofsFileSystemException()
    {
    }

    public ErofsFileSystemException(string? message) : base(message)
    {
    }

    public ErofsFileSystemException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}