using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

internal static class FirehosePayloadLimits
{
    internal static int GetTransferBufferSize(FirehoseConfigureResponse configuration)
    {
        ulong value = configuration.MaxPayloadSizeToTargetInBytes;
        ulong supported = configuration.MaxPayloadSizeToTargetInBytesSupported;
        if (value == 0) value = supported;
        else if (supported > 0) value = Math.Min(value, supported);
        if (value is 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(configuration));
        return checked((int)value);
    }
}
