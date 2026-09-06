using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.ImageFormats.Abstractions;

public sealed record ImageFormatDiagnostic
{
    public ImageFormatDiagnostic(
        string formatId,
        string? structure = null,
        string? field = null,
        BlockDeviceId? blockDeviceId = null,
        long? deviceRelativeOffset = null,
        long? logicalOffset = null,
        string? mappingPath = null,
        string? objectId = null,
        ulong? featureId = null,
        ImageFormatDiagnosticReason? reason = null,
        string? resourceKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatId);

        if (deviceRelativeOffset is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceRelativeOffset));
        }

        if (logicalOffset is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalOffset));
        }

        if (reason.HasValue && !Enum.IsDefined(reason.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (resourceKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        }

        FormatId = formatId;
        Structure = structure;
        Field = field;
        BlockDeviceId = blockDeviceId;
        DeviceRelativeOffset = deviceRelativeOffset;
        LogicalOffset = logicalOffset;
        MappingPath = mappingPath;
        ObjectId = objectId;
        FeatureId = featureId;
        Reason = reason;
        ResourceKey = resourceKey;
    }

    public string FormatId { get; }
    public string? Structure { get; }
    public string? Field { get; }
    public BlockDeviceId? BlockDeviceId { get; }
    public long? DeviceRelativeOffset { get; }
    public long? LogicalOffset { get; }
    public string? MappingPath { get; }
    public string? ObjectId { get; }
    public ulong? FeatureId { get; }
    public ImageFormatDiagnosticReason? Reason { get; }
    public string? ResourceKey { get; }
}
