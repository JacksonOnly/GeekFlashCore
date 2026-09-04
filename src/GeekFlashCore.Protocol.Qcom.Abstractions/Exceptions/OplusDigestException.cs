namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public class OplusDigestException : FirehoseProtocolException
{
    public OplusDigestException(string? message) : base(message)
    {
    }

    public OplusDigestException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
