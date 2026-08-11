using GeekFlashCore.Abstractions;

namespace GeekFlashCore.Gpt.Abstractions;

public sealed class GptException : GeekFlashCoreException
{
    public GptException()
    {
    }

    public GptException(string? message)
        : base(message)
    {
    }

    public GptException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
