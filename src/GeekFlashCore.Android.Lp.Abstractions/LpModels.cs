using GeekFlashCore.ImageFormats.Abstractions;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp.Abstractions;

public static class LpFormat
{
    public const uint GeometryMagic = 0x616C4467;
    public const int GeometryBlockSize = 4096;
    public const int GeometryStructSize = 52;
    public const uint HeaderMagic = 0x414C5030;
    public const ushort SupportedMajorVersion = 10;
    public const ushort MaximumMinorVersion = 2;
    public const int SectorSize = 512;
    public const int ReservedBytes = 4096;
    public const int HeaderV10Size = 128;
    public const int HeaderV12Size = 256;
    public const int PartitionEntrySize = 52;
    public const int ExtentEntrySize = 24;
    public const int GroupEntrySize = 48;
    public const int BlockDeviceEntrySize = 64;
}

public enum LpMetadataCopyKind
{
    Primary,
    Backup
}

public enum LpCopyValidationStatus
{
    Valid,
    Invalid
}

public enum LpExtentTargetType : uint
{
    Linear = 0,
    Zero = 1
}

[Flags]
public enum LpPartitionAttributes : uint
{
    None = 0,
    ReadOnly = 1 << 0,
    SlotSuffixed = 1 << 1,
    Updated = 1 << 2,
    Disabled = 1 << 3
}

[Flags]
public enum LpGroupFlags : uint
{
    None = 0,
    SlotSuffixed = 1 << 0
}

[Flags]
public enum LpBlockDeviceFlags : uint
{
    None = 0,
    SlotSuffixed = 1 << 0
}

public readonly record struct LpGeometry(
    uint MetadataMaxSize,
    uint MetadataSlotCount,
    uint LogicalBlockSize);

public sealed record LpGeometryCopy(
    LpMetadataCopyKind Kind,
    long Offset,
    LpCopyValidationStatus Status,
    LpGeometry? Geometry,
    ImageFormatDiagnostic? Diagnostic);

public readonly record struct LpTableDescriptor(
    uint Offset,
    uint EntryCount,
    uint EntrySize);

public sealed record LpMetadataHeader(
    ushort MajorVersion,
    ushort MinorVersion,
    uint HeaderSize,
    uint TablesSize,
    LpTableDescriptor Partitions,
    LpTableDescriptor Extents,
    LpTableDescriptor Groups,
    LpTableDescriptor BlockDevices,
    uint Flags);

public sealed record LpMetadataCopySummary(
    int SlotNumber,
    LpMetadataCopyKind Kind,
    long Offset,
    LpCopyValidationStatus Status,
    LpMetadataHeader? Header,
    ImageFormatDiagnostic? Diagnostic);

public sealed record LpMetadataSlotSummary(
    int SlotNumber,
    LpMetadataCopySummary Primary,
    LpMetadataCopySummary Backup)
{
    public LpMetadataCopySummary? Preferred => Primary.Status == LpCopyValidationStatus.Valid
        ? Primary
        : Backup.Status == LpCopyValidationStatus.Valid
            ? Backup
            : null;
}

public readonly record struct LpPartition(
    string RawName,
    string Name,
    LpPartitionAttributes Attributes,
    uint FirstExtentIndex,
    uint ExtentCount,
    uint GroupIndex,
    long LogicalSize);

public readonly record struct LpExtent(
    ulong SectorCount,
    LpExtentTargetType TargetType,
    ulong TargetData,
    uint TargetSource);

public readonly record struct LpPartitionGroup(
    string RawName,
    string Name,
    LpGroupFlags Flags,
    ulong MaximumSize);

public readonly record struct LpBlockDevice(
    ulong FirstLogicalSector,
    uint Alignment,
    uint AlignmentOffset,
    ulong Size,
    string RawPartitionName,
    string PartitionName,
    LpBlockDeviceFlags Flags);

public interface ILpBlockDeviceResolver
{
    ValueTask<IReadableBlockDeviceLease> ResolveAsync(
        LpBlockDevice blockDevice,
        CancellationToken cancellationToken = default);
}
