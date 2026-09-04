namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public class FirehoseConfigureException : FirehoseProtocolException
{
    public FirehoseConfigureException(string? message, FirehoseCommandResult? result = null) : base(message)
    {
        Result = result;
    }

    public FirehoseConfigureException(
        string? message,
        Exception? innerException,
        FirehoseCommandResult? result = null) : base(message, innerException)
    {
        Result = result;
    }

    public FirehoseCommandResult? Result { get; }
}
