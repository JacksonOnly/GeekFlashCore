namespace GeekFlashCore.FileSystem.Abstractions;

public sealed record FileSystemProbeResult
{
    public FileSystemProbeResult(
        FileSystemProbeStatus status,
        string? formatId = null,
        string? resourceKey = null,
        int confidence = 0,
        long? declaredLength = null,
        ReadOnlyMemory<ulong> unsupportedFeatures = default)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (confidence is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(confidence));
        if (declaredLength is < 0) throw new ArgumentOutOfRangeException(nameof(declaredLength));
        if (status != FileSystemProbeStatus.NotRecognized)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        }

        Status = status;
        FormatId = formatId;
        ResourceKey = resourceKey;
        Confidence = confidence;
        DeclaredLength = declaredLength;
        UnsupportedFeatures = unsupportedFeatures;
    }

    public FileSystemProbeStatus Status { get; }
    public string? FormatId { get; }
    public string? ResourceKey { get; }
    public int Confidence { get; }
    public long? DeclaredLength { get; }
    public ReadOnlyMemory<ulong> UnsupportedFeatures { get; }
    public bool IsBrowsable => Status == FileSystemProbeStatus.RecognizedSupported;

    public static FileSystemProbeResult NotRecognized { get; } =
        new(FileSystemProbeStatus.NotRecognized);
}
