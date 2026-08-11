using GeekFlashCore.FileSystem.Abstractions;

namespace GeekFlashCore.FileSystem.Ext;

public class ExtFileSystemException : FileSystemException
{
    public ExtFileSystemException()
    {
    }

    public ExtFileSystemException(string? message) : base(message)
    {
    }

    public ExtFileSystemException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}