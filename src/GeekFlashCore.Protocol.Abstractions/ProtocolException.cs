using GeekFlashCore.Abstractions;

namespace GeekFlashCore.Protocol.Abstractions;

public class ProtocolException : GeekFlashCoreException
{
    public ProtocolException()
    {
    }

    public ProtocolException(string? message) : base(message)
    {
    }

    public ProtocolException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}