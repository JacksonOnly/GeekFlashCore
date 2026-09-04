namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public class QcomAuthenticationException : FirehoseProtocolException
{
    public QcomAuthenticationException(string? message) : base(message)
    {
    }

    public QcomAuthenticationException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
