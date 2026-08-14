namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public class SaharaProtocolException : QcomProtocolException
{
    public SaharaProtocolException()
    {
    }

    public SaharaProtocolException(string? message) : base(message)
    {
    }

    public SaharaProtocolException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}