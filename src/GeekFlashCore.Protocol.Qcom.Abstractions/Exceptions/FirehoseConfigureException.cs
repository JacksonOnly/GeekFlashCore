namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public class FirehoseConfigureException : FirehoseProtocolException
{
    public FirehoseConfigureException(string? message) : base(message)
    {
    }

    public FirehoseConfigureException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
