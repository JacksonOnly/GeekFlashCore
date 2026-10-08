using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Optional standard DA operations; no extension, patch or exploitation is required.</summary>
public interface IMtkDaDiagnostics
{
    /// <summary>Queries one allowlisted standard property. Dispose the owned result to clear it.</summary>
    MtkSensitiveBuffer QueryDa(MtkDaQuery query, CancellationToken cancellationToken = default);
    /// <summary>Retrieves a bounded XML system property without opening device-supplied host paths.</summary>
    MtkSensitiveBuffer GetDaSystemProperty(string key, CancellationToken cancellationToken = default);
    /// <summary>Reads a single explicitly addressed, aligned register through Legacy or XML commands.</summary>
    uint ReadDaRegister(uint address, CancellationToken cancellationToken = default);
    /// <summary>Writes a single explicitly addressed, aligned register through Legacy or XML commands.</summary>
    void WriteDaRegister(uint address, uint value, CancellationToken cancellationToken = default);
    /// <summary>Reads the Legacy PMT using an explicit runtime layout or the validated eMMC disk format.</summary>
    IReadOnlyList<PartitionInfo> GetLegacyPartitionTable(MtkPmtLayout layout, CancellationToken cancellationToken = default);
    /// <summary>Reads a bounded native XML partition table and validates all user-region ranges.</summary>
    IReadOnlyList<PartitionInfo> GetXmlPartitionTable(CancellationToken cancellationToken = default);
}

/// <summary>Allowlisted read-only XFlash controls. XML implements firmware/hardware information and version.</summary>
public enum MtkDaQuery : uint
{
    EmmcInfo = (uint)MtkXFlashCommand.GetEmmcInfo,
    NandInfo = (uint)MtkXFlashCommand.GetNandInfo,
    NorInfo = (uint)MtkXFlashCommand.GetNorInfo,
    UfsInfo = (uint)MtkXFlashCommand.GetUfsInfo,
    Version = (uint)MtkXFlashCommand.GetDaVersion,
    Expiration = (uint)MtkXFlashCommand.GetExpireData,
    PacketLength = (uint)MtkXFlashCommand.GetPacketLength,
    RandomId = (uint)MtkXFlashCommand.GetRandomId,
    PartitionTableCategory = (uint)MtkXFlashCommand.GetPartitionTblCata,
    ConnectionAgent = (uint)MtkXFlashCommand.GetConnectionAgent,
    UsbSpeed = (uint)MtkXFlashCommand.GetUsbSpeed,
    RamInfo = (uint)MtkXFlashCommand.GetRamInfo,
    ChipId = (uint)MtkXFlashCommand.GetChipId,
    OtpLockStatus = (uint)MtkXFlashCommand.GetOtpLockStatus,
    BatteryVoltage = (uint)MtkXFlashCommand.GetBatteryVoltage,
    RpmbStatus = (uint)MtkXFlashCommand.GetRpmbStatus,
    ExpirationDate = (uint)MtkXFlashCommand.GetExpireDate,
    DramType = (uint)MtkXFlashCommand.GetDramType,
    DeviceFirmwareInfo = (uint)MtkXFlashCommand.GetDevFwInfo,
    HardwareId = (uint)MtkXFlashCommand.GetHrid,
    ErrorDetail = (uint)MtkXFlashCommand.GetErrorDetail,
    SlaStatus = (uint)MtkXFlashCommand.SlaEnabledStatus,
    /// <summary>Synthetic cross-dialect query, never sent as an XFlash command.</summary>
    HardwareInfo = 0x1000000
}

/// <summary>Legacy READ_PMT layouts (76/88/96-byte entries), or a versioned eMMC USER disk table.</summary>
public enum MtkPmtLayout
{
    Word32, Word64, Legacy96,
    /// <summary>4096-byte PT/MPT v1.0 blocks at the USER tail, with forty 88-byte entries. Requires 512-byte eMMC sectors.</summary>
    DiskV1
}
