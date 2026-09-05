namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public static class FirehoseConstants
{
    public const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><data>";
    public const string XmlDataEnd = "</data>";
    public const int InitialXmlBufferSize = 4096;
    public const int MaximumXmlPacketSize = 1024 * 1024;
    // Raw Firehose payloads are streamed, so this bound protects declared
    // lengths without coupling generic Digest transfers to Oplus' 4 MiB table
    // format limit.
    public const long MaximumRawTransferLength = int.MaxValue;
    
    public const int DefaultPayloadSize = 1024 * 1024;
    public const ulong DefaultMaxDigestTableSize = 8192;
    public const int MaxConfigureAttempts = 6;
    public const int MaximumPhysicalPartitionCount = 8;
}
