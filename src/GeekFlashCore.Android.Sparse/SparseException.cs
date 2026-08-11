using GeekFlashCore.Abstractions;

namespace GeekFlashCore.Android.Sparse;

public sealed class SparseException : GeekFlashCoreException
{
    public SparseException()
    {
    }

    public SparseException(string? message)
        : base(message)
    {
    }

    public SparseException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
