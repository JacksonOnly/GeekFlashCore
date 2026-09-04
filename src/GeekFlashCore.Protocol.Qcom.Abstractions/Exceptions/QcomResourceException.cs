namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public class QcomResourceException : QcomProtocolException
{
    public QcomResourceException(string? message) : base(message)
    {
    }

    public QcomResourceException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
