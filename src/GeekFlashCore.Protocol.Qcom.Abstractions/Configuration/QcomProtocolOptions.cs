namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record QcomProtocolOptions
{
    public const int DefaultConnectTimeoutMilliseconds = 1_500;
    public const int DefaultReadTimeoutMilliseconds = 10_000;
    public const int DefaultWriteTimeoutMilliseconds = 10_000;
    public const int DefaultResourceRequestTimeoutMilliseconds = 15_000;

    public int ConnectTimeoutMilliseconds { get; init; } = DefaultConnectTimeoutMilliseconds;
    public int ReadTimeoutMilliseconds { get; init; } = DefaultReadTimeoutMilliseconds;
    public int WriteTimeoutMilliseconds { get; init; } = DefaultWriteTimeoutMilliseconds;
    public int ResourceRequestTimeoutMilliseconds { get; init; } = DefaultResourceRequestTimeoutMilliseconds;
    /// <summary>Send qdl-compatible Sahara HELLO response when the first protocol read times out.</summary>
    public bool ProbeFirehoseOnSaharaTimeout { get; init; } = true;
    public QcomVendorKind VendorOverride { get; init; } = QcomVendorKind.Auto;
    /// <summary>Overrides the authentication material mode for legacy/provider-backed flows. OnePlus and Nothing use Core algorithms automatically.</summary>
    public QcomAuthenticationKind? AuthenticationKind { get; init; }
    /// <summary>Optional comma-separated OnePlus project IDs. When omitted, Core reads param and then enumerates built-in profiles.</summary>
    public string? OnePlusProjectId { get; init; }
    public FirehoseConfiguration Firehose { get; init; } = new();
    public FirehoseDigestConfiguration FirehoseDigest { get; init; } = new();
    public FirehoseVipConfiguration FirehoseVip { get; init; } = new();
    public OplusDigestConfiguration OplusDigest { get; init; } = new();

    public void Validate()
    {
        ValidateTimeout(ConnectTimeoutMilliseconds, nameof(ConnectTimeoutMilliseconds));
        ValidateTimeout(ReadTimeoutMilliseconds, nameof(ReadTimeoutMilliseconds));
        ValidateTimeout(WriteTimeoutMilliseconds, nameof(WriteTimeoutMilliseconds));
        ValidateTimeout(ResourceRequestTimeoutMilliseconds, nameof(ResourceRequestTimeoutMilliseconds));
        ArgumentNullException.ThrowIfNull(Firehose);
        ArgumentNullException.ThrowIfNull(FirehoseDigest);
        ArgumentNullException.ThrowIfNull(FirehoseVip);
        ArgumentNullException.ThrowIfNull(OplusDigest);
        if (AuthenticationKind is { } authentication && !Enum.IsDefined(authentication))
            throw new ArgumentOutOfRangeException(nameof(AuthenticationKind), authentication, "Unknown authentication kind.");
        if (OnePlusProjectId?.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(OnePlusProjectId));
        Firehose.Validate();
        FirehoseDigest.Validate();
        FirehoseVip.Validate();
        OplusDigest.Validate();
        if (FirehoseDigest.Enabled && OplusDigest.Mode != OplusDigestMode.None)
            throw new ArgumentException(Strings.GenericDigestAndOplusConflict);
        if (FirehoseDigest.Enabled && FirehoseVip.Enabled)
            throw new ArgumentException(Strings.GenericDigestAndVipConflict);
        if (FirehoseVip.Enabled && OplusDigest.Mode != OplusDigestMode.None)
            throw new ArgumentException(Strings.VipAndOplusConflict);
    }

    private static void ValidateTimeout(int value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, "Timeout must be positive.");
    }
}

public sealed record FirehoseDigestConfiguration
{
    /// <summary>Enables the generic digest exchange during connection.</summary>
    public bool Enabled { get; init; }

    /// <summary>When enabled, send the table after Firehose startup and before Configure.</summary>
    public bool SendOnConnect { get; init; } = true;

    public void Validate()
    {
        // The resource provider performs the source length validation. Keep this
        // object intentionally small so it can be extended without coupling it
        // to vendor-specific digest modes.
    }
}

public sealed record FirehoseVipConfiguration
{
    public const int SignedTableFrameCapacity = 54;
    public const int ChainedTableFrameCapacity = 256;

    /// <summary>Enables qdl-compatible VIP table transfer.</summary>
    public bool Enabled { get; init; }

    /// <summary>Require the programmer startup marker before sending VIP tables.</summary>
    public bool RequireStartupMarker { get; init; } = true;

    public void Validate()
    {
        if (SignedTableFrameCapacity <= 0 || ChainedTableFrameCapacity <= 0)
            throw new InvalidOperationException(Strings.VipFrameCapacitiesPositive);
    }
}
