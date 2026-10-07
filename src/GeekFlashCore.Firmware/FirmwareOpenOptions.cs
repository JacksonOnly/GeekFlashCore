using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware;

/// <summary>Limits applied before metadata allocation and index construction.</summary>
public sealed record FirmwareOpenOptions
{
    /// <summary>Explicit format, or bounded automatic detection.</summary>
    public FirmwareFormat Format { get; init; } = FirmwareFormat.Auto;
    /// <summary>Maximum number of catalog entries.</summary>
    public int MaximumEntries { get; init; } = 65536;
    /// <summary>Maximum serialized metadata size, including ZIP's central directory.</summary>
    public int MaximumMetadataBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Maximum total payload operations and extents, or OZIP block descriptors.</summary>
    public int MaximumSegments { get; init; } = 262144;
    /// <summary>Buffer used for copying and replaying compressed streams.</summary>
    public int BufferSize { get; init; } = 64 * 1024;
    /// <summary>Maximum XZ dictionary or Zstandard window size for one payload decoder.</summary>
    public int MaximumDecoderWindowBytes { get; init; } = 64 * 1024 * 1024;

    internal void Validate()
    {
        if (!Enum.IsDefined(Format) || MaximumEntries is < 1 or > 1000000 ||
            MaximumMetadataBytes is < 512 or > 128 * 1024 * 1024 ||
            MaximumSegments is < 1 or > 1000000 || BufferSize is < 4096 or > 1024 * 1024 ||
            MaximumDecoderWindowBytes is < 1024 * 1024 or > 256 * 1024 * 1024 ||
            (MaximumDecoderWindowBytes & (MaximumDecoderWindowBytes - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(FirmwareOpenOptions), Strings.InvalidOptions);
    }
}
