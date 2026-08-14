namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public class FirehoseProtocolException : QcomProtocolException
{
    public FirehoseProtocolException()
    {
    }

    public FirehoseProtocolException(string? message) : base(message)
    {
    }

    public FirehoseProtocolException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}