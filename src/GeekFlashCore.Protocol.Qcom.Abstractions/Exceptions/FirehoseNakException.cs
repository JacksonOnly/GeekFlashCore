namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public class FirehoseNakException : FirehoseProtocolException
{
    public FirehoseNakException(string? message, FirehoseCommandResult result) : base(message)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public FirehoseCommandResult Result { get; }
}
