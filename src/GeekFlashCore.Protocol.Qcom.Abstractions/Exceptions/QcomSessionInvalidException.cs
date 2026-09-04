namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public class QcomSessionInvalidException : QcomProtocolException
{
    public QcomSessionInvalidException(string? message) : base(message)
    {
    }

    public QcomSessionInvalidException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
