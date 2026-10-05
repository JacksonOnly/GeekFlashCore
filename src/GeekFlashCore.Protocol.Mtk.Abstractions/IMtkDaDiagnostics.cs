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
    /// <summary>Reads the Legacy PMT using the host-confirmed entry layout; no heuristic format guessing.</summary>
    IReadOnlyList<PartitionInfo> GetLegacyPartitionTable(MtkPmtLayout layout, CancellationToken cancellationToken = default);
    /// <summary>Reads a bounded native XML partition table and validates all user-region ranges.</summary>
    IReadOnlyList<PartitionInfo> GetXmlPartitionTable(CancellationToken cancellationToken = default);
}

/// <summary>Allowlisted read-only XFlash controls. XML implements firmware/hardware information and version.</summary>
public enum MtkDaQuery : uint
{
    EmmcInfo = 0x40001, NandInfo = 0x40002, NorInfo = 0x40003, UfsInfo = 0x40004,
    Version = 0x40005, Expiration = 0x40006, PacketLength = 0x40007, RandomId = 0x40008,
    PartitionTableCategory = 0x40009, ConnectionAgent = 0x4000a, UsbSpeed = 0x4000b,
    RamInfo = 0x4000c, ChipId = 0x4000d, OtpLockStatus = 0x4000e, BatteryVoltage = 0x4000f,
    RpmbStatus = 0x40010, ExpirationDate = 0x40011, DramType = 0x40012, DeviceFirmwareInfo = 0x40013,
    HardwareId = 0x40014, ErrorDetail = 0x40015, SlaStatus = 0x40016,
    HardwareInfo = 0x1000000
}

/// <summary>Host-confirmed Legacy PMT layouts: 76-byte, 88-byte or 96-byte entries.</summary>
public enum MtkPmtLayout { Word32, Word64, Legacy96 }
