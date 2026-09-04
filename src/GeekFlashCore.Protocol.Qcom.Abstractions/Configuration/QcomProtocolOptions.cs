namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record QcomProtocolOptions
{
    public const int DefaultConnectTimeoutMilliseconds = 10_000;
    public const int DefaultReadTimeoutMilliseconds = 10_000;
    public const int DefaultWriteTimeoutMilliseconds = 10_000;
    public const int DefaultResourceRequestTimeoutMilliseconds = 15_000;

    public int ConnectTimeoutMilliseconds { get; init; } = DefaultConnectTimeoutMilliseconds;
    public int ReadTimeoutMilliseconds { get; init; } = DefaultReadTimeoutMilliseconds;
    public int WriteTimeoutMilliseconds { get; init; } = DefaultWriteTimeoutMilliseconds;
    public int ResourceRequestTimeoutMilliseconds { get; init; } = DefaultResourceRequestTimeoutMilliseconds;
    public QcomVendorKind VendorOverride { get; init; } = QcomVendorKind.Auto;
    public FirehoseConfiguration Firehose { get; init; } = new();
    public OplusDigestConfiguration OplusDigest { get; init; } = new();

    public void Validate()
    {
        ValidateTimeout(ConnectTimeoutMilliseconds, nameof(ConnectTimeoutMilliseconds));
        ValidateTimeout(ReadTimeoutMilliseconds, nameof(ReadTimeoutMilliseconds));
        ValidateTimeout(WriteTimeoutMilliseconds, nameof(WriteTimeoutMilliseconds));
        ValidateTimeout(ResourceRequestTimeoutMilliseconds, nameof(ResourceRequestTimeoutMilliseconds));
        ArgumentNullException.ThrowIfNull(Firehose);
        ArgumentNullException.ThrowIfNull(OplusDigest);
        Firehose.Validate();
        OplusDigest.Validate();
    }

    private static void ValidateTimeout(int value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, "Timeout must be positive.");
    }
}
