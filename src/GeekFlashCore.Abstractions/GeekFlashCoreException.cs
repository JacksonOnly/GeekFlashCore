namespace GeekFlashCore.Abstractions;

public abstract class GeekFlashCoreException : Exception
{
    protected GeekFlashCoreException()
    {
    }

    protected GeekFlashCoreException(string? message)
        : base(message)
    {
    }

    protected GeekFlashCoreException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
