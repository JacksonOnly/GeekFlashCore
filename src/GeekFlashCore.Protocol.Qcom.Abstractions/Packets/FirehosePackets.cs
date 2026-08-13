using System.Xml.Serialization;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public record BaseCommand
{
}

public interface IFirehoseIoOptions
{
    public ulong? LastSector { get; set; }
    public byte? SkipBadBlock { get; set; }
    public byte? GetSpare { get; set; }
    public byte? EccDisabled { get; set; }
}

public interface IFirehoseDevData
{
    public FirehoseStorage? Storage { get; set; }
    public uint? Slot { get; set; }
    public uint? PhysicalPartitionNumber { get; set; }
}

public interface IFirehoseIoData
{
    public uint SectorSizeInBytes { get; set; }
    public string NumPartitionSectors { get; set; }
    public string StartSector { get; set; }
}

[FirehoseCmdTag("program")]
public record ProgramCommand : BaseCommand, IFirehoseDevData, IFirehoseIoData, IFirehoseIoOptions
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("SECTOR_SIZE_IN_BYTES")]
    public uint SectorSizeInBytes { get; set; }

    [FirehoseCmdAttribute("num_partition_sectors")]
    public string NumPartitionSectors { get; set; } = string.Empty;

    [FirehoseCmdAttribute("start_sector")] public string StartSector { get; set; } = string.Empty;

    [FirehoseCmdAttribute("skip_bad_block")]
    public byte? SkipBadBlock { get; set; }

    [FirehoseCmdAttribute("get_spare")] public byte? GetSpare { get; set; }
    [FirehoseCmdAttribute("ecc_disabled")] public byte? EccDisabled { get; set; }
    [FirehoseCmdAttribute("last_sector")] public ulong? LastSector { get; set; }
}

[FirehoseCmdTag("read")]
public record ReadCommand : BaseCommand, IFirehoseDevData, IFirehoseIoData, IFirehoseIoOptions
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("SECTOR_SIZE_IN_BYTES")]
    public uint SectorSizeInBytes { get; set; }

    [FirehoseCmdAttribute("num_partition_sectors")]
    public string NumPartitionSectors { get; set; } = string.Empty;

    [FirehoseCmdAttribute("start_sector")] public string StartSector { get; set; } = string.Empty;

    [FirehoseCmdAttribute("skip_bad_block")]
    public byte? SkipBadBlock { get; set; }

    [FirehoseCmdAttribute("get_spare")] public byte? GetSpare { get; set; }
    [FirehoseCmdAttribute("ecc_disabled")] public byte? EccDisabled { get; set; }
    [FirehoseCmdAttribute("last_sector")] public ulong? LastSector { get; set; }
}

[FirehoseCmdTag("nop")]
public record NopCommand : BaseCommand;

[FirehoseCmdTag("patch")]
public record PatchCommand : BaseCommand, IFirehoseDevData
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("filename")] public string FileName { get; set; } = string.Empty;

    [FirehoseCmdAttribute("SECTOR_SIZE_IN_BYTES")]
    public uint SectorSizeInBytes { get; set; }

    [FirehoseCmdAttribute("start_sector")] public string StartSector { get; set; } = string.Empty;
    [FirehoseCmdAttribute("byte_offset")] public uint ByteOffset { get; set; }

    [FirehoseCmdAttribute("size_in_bytes")]
    public uint SizeInBytes { get; set; }

    [FirehoseCmdAttribute("value")] public string Value { get; set; } = string.Empty;
}

[FirehoseCmdTag("configure")]
public record ConfigureCommand : BaseCommand
{
    [FirehoseCmdAttribute("MemoryName")] public FirehoseStorage? MemoryName { get; set; }
    [FirehoseCmdAttribute("Verbose")] public byte? Verbose { get; set; }

    [FirehoseCmdAttribute("MaxPayloadSizeToTargetInBytes")]
    public ulong? MaxPayloadSizeToTargetInBytes { get; set; }

    [FirehoseCmdAttribute("AlwaysValidate")]
    public byte? AlwaysValidate { get; set; }

    [FirehoseCmdAttribute("MaxDigestTableSizeInBytes")]
    public ulong? MaxDigestTableSizeInBytes { get; set; }

    [FirehoseCmdAttribute("ZlpAwareHost")] public byte? ZlpAwareHost { get; set; }
    [FirehoseCmdAttribute("SkipWrite")] public byte? SkipWrite { get; set; }
}

[FirehoseCmdTag("setbootablestoragedrive")]
public record SetBootableStorageDriveCommand : BaseCommand
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("value")] public uint Value { get; set; }
}

[FirehoseCmdTag("erase")]
public record EraseCommand : BaseCommand, IFirehoseDevData, IFirehoseIoData, IFirehoseIoOptions
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("SECTOR_SIZE_IN_BYTES")]
    public uint SectorSizeInBytes { get; set; }

    [FirehoseCmdAttribute("num_partition_sectors")]
    public string NumPartitionSectors { get; set; } = string.Empty;

    [FirehoseCmdAttribute("start_sector")] public string StartSector { get; set; } = string.Empty;

    [FirehoseCmdAttribute("skip_bad_block")]
    public byte? SkipBadBlock { get; set; }

    [FirehoseCmdAttribute("get_spare")] public byte? GetSpare { get; set; }
    [FirehoseCmdAttribute("ecc_disabled")] public byte? EccDisabled { get; set; }
    [FirehoseCmdAttribute("last_sector")] public ulong? LastSector { get; set; }
}

[FirehoseCmdTag("power")]
public record PowerCommand : BaseCommand
{
    [FirehoseCmdAttribute("value")] public FirehosePowerValue Value { get; set; }

    [FirehoseCmdAttribute("DelayInSeconds")]
    public ulong? DelayInSeconds { get; set; }
}

[FirehoseCmdTag("firmwarewrite")]
public record FirmwareWriteCommand : BaseCommand, IFirehoseDevData
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("SECTOR_SIZE_IN_BYTES")]
    public uint SectorSizeInBytes { get; set; } // 必须为1

    [FirehoseCmdAttribute("num_partition_sectors")]
    public ulong NumPartitionSectors { get; set; }
}

[FirehoseCmdTag("getstorageinfo")]
public record GetStorageInfoCommand : BaseCommand, IFirehoseDevData
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("print_json")] public ulong? PrintJson { get; set; } // 0 或 1
}

[FirehoseCmdTag("benchmark")]
public record BenchmarkCommand : BaseCommand, IFirehoseDevData, IFirehoseIoOptions
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("trials")] public uint? Trials { get; set; }

    [FirehoseCmdAttribute("TestDigestPerformance")]
    public uint? TestDigestPerformance { get; set; }

    [FirehoseCmdAttribute("TestWritePerformance")]
    public uint? TestWritePerformance { get; set; }

    [FirehoseCmdAttribute("TestReadPerformance")]
    public uint? TestReadPerformance { get; set; }

    [FirehoseCmdAttribute("last_sector")] public ulong? LastSector { get; set; }

    [FirehoseCmdAttribute("skip_bad_block")]
    public byte? SkipBadBlock { get; set; }

    [FirehoseCmdAttribute("get_spare")] public byte? GetSpare { get; set; }
    [FirehoseCmdAttribute("ecc_disabled")] public byte? EccDisabled { get; set; }
}

[FirehoseCmdTag("peek")]
public record PeekCommand : BaseCommand
{
    [FirehoseCmdAttribute("size_in_bytes")]
    public ulong SizeInBytes { get; set; }

    [FirehoseCmdAttribute("address64")] public string Address64 { get; set; } = string.Empty;
}

[FirehoseCmdTag("emmc")]
public record EmmcCommand : BaseCommand, IFirehoseDevData
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("DRIVE4_SIZE_IN_KB")]
    public uint? Drive4SizeInKb { get; set; }

    [FirehoseCmdAttribute("DRIVE5_SIZE_IN_KB")]
    public uint? Drive5SizeInKb { get; set; }

    [FirehoseCmdAttribute("DRIVE6_SIZE_IN_KB")]
    public uint? Drive6SizeInKb { get; set; }

    [FirehoseCmdAttribute("DRIVE7_SIZE_IN_KB")]
    public uint? Drive7SizeInKb { get; set; }

    [FirehoseCmdAttribute("ENH_SIZE")] public uint? EnhSize { get; set; }

    [FirehoseCmdAttribute("ENH_START_ADDR")]
    public uint? EnhStartAddr { get; set; }

    [FirehoseCmdAttribute("GPP_ENH_FLAG")] public byte? GppEnhFlag { get; set; }
    [FirehoseCmdAttribute("commit")] public uint? Commit { get; set; }
}

[FirehoseCmdTag("ufs")]
public record UfsCommand : BaseCommand, IFirehoseDevData
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    // LUN-specific attributes (if LUNum is provided)
    [FirehoseCmdAttribute("LUNum")] public uint? LunNum { get; set; }
    [FirehoseCmdAttribute("bLUEnable")] public byte? BLuEnable { get; set; }
    [FirehoseCmdAttribute("bBootLunID")] public byte? BBootLunId { get; set; }

    [FirehoseCmdAttribute("bLUWriteProtect")]
    public byte? BLuWriteProtect { get; set; }

    [FirehoseCmdAttribute("bMemoryType")] public byte? BMemoryType { get; set; }
    [FirehoseCmdAttribute("size_in_kb")] public ulong? SizeInKb { get; set; }

    [FirehoseCmdAttribute("bDataReliability")]
    public byte? BDataReliability { get; set; }

    [FirehoseCmdAttribute("bLogicalBlockSize")]
    public byte? BLogicalBlockSize { get; set; }

    [FirehoseCmdAttribute("bProvisioningType")]
    public byte? BProvisioningType { get; set; }

    [FirehoseCmdAttribute("wContextCapabilities")]
    public ulong? WContextCapabilities { get; set; }

    [FirehoseCmdAttribute("wb_buffer_size_in_kb")]
    public ulong? WbBufferSizeInKb { get; set; }

    [FirehoseCmdAttribute("wLUMaxActiveHPBRegions")]
    public ushort? WLumaxActiveHpbRegions { get; set; }

    [FirehoseCmdAttribute("wHPBPinnedRegionStartIdx")]
    public ushort? WHpbPinnedRegionStartIdx { get; set; }

    [FirehoseCmdAttribute("wNumHPBPinnedRegions")]
    public ushort? WNumHpbPinnedRegions { get; set; }

    // Global attributes (if LUNum not present)
    [FirehoseCmdAttribute("bBootEnable")] public byte? BBootEnable { get; set; }

    [FirehoseCmdAttribute("bDescrAccessEn")]
    public byte? BDescrAccessEn { get; set; }

    [FirehoseCmdAttribute("bInitPowerMode")]
    public byte? BInitPowerMode { get; set; }

    [FirehoseCmdAttribute("bHighPriorityLUN")]
    public byte? BHighPriorityLun { get; set; }

    [FirehoseCmdAttribute("bSecureRemovalType")]
    public byte? BSecureRemovalType { get; set; }

    [FirehoseCmdAttribute("bInitActiveICCLevel")]
    public byte? BInitActiveIccLevel { get; set; }

    [FirehoseCmdAttribute("wPeriodicRTCUpdate")]
    public ushort? WPeriodicRtcUpdate { get; set; }

    [FirehoseCmdAttribute("bHPBControl")] public byte? BHpbControl { get; set; }

    [FirehoseCmdAttribute("bRPMBRegionEnable")]
    public byte? BRpmbRegionEnable { get; set; }

    [FirehoseCmdAttribute("bRPMBRegion1Size")]
    public byte? BRpmbRegion1Size { get; set; }

    [FirehoseCmdAttribute("bRPMBRegion2Size")]
    public byte? BRpmbRegion2Size { get; set; }

    [FirehoseCmdAttribute("bRPMBRegion3Size")]
    public byte? BRpmbRegion3Size { get; set; }

    [FirehoseCmdAttribute("bWriteBoosterBufferPreserveUserSpaceEn")]
    public byte? BWriteBoosterBufferPreserveUserSpaceEn { get; set; }

    [FirehoseCmdAttribute("bWriteBoosterBufferType")]
    public byte? BWriteBoosterBufferType { get; set; }

    [FirehoseCmdAttribute("shared_wb_buffer_size_in_kb")]
    public ulong? SharedWbBufferSizeInKb { get; set; }

    [FirehoseCmdAttribute("bConfigDescrLock")]
    public byte? BConfigDescrLock { get; set; }

    [FirehoseCmdAttribute("qVendorConfigCode")]
    public uint? QVendorConfigCode { get; set; }

    [FirehoseCmdAttribute("LUNtoGrow")] public string? LunToGrow { get; set; } // "-1" or number
    [FirehoseCmdAttribute("commit")] public uint? Commit { get; set; }
}

[FirehoseCmdTag("fixgpt")]
public record FixGptCommand : BaseCommand, IFirehoseDevData
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("grow_last_partition")]
    public byte? GrowLastPartition { get; set; } // 默认1

    [FirehoseCmdAttribute("lun")] public string Lun { get; set; } = string.Empty; // "all" or number
}

[FirehoseCmdTag("getsha256digest")]
public record GetSha256DigestCommand : BaseCommand, IFirehoseDevData, IFirehoseIoOptions, IFirehoseIoData
{
    [FirehoseCmdAttribute("storage_type")] public FirehoseStorage? Storage { get; set; }
    [FirehoseCmdAttribute("slot")] public uint? Slot { get; set; }

    [FirehoseCmdAttribute("physical_partition_number")]
    public uint? PhysicalPartitionNumber { get; set; }

    [FirehoseCmdAttribute("SECTOR_SIZE_IN_BYTES")]
    public uint SectorSizeInBytes { get; set; }

    [FirehoseCmdAttribute("num_partition_sectors")]
    public string NumPartitionSectors { get; set; } = string.Empty;

    [FirehoseCmdAttribute("start_sector")] public string StartSector { get; set; } = string.Empty;

    [FirehoseCmdAttribute("skip_bad_block")]
    public byte? SkipBadBlock { get; set; }

    [FirehoseCmdAttribute("get_spare")] public byte? GetSpare { get; set; }
    [FirehoseCmdAttribute("ecc_disabled")] public byte? EccDisabled { get; set; }
    [FirehoseCmdAttribute("last_sector")] public ulong? LastSector { get; set; }
}