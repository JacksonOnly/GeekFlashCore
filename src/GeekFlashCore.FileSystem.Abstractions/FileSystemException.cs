using GeekFlashCore.Abstractions;

namespace GeekFlashCore.FileSystem.Abstractions;

public class FileSystemException : GeekFlashCoreException
{
    public FileSystemException()
    {
    }

    public FileSystemException(string? message)
        : base(message)
    {
    }

    public FileSystemException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}