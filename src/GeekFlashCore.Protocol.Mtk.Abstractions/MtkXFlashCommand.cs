// SPDX-License-Identifier: AGPL-3.0-or-later
// Command values: penumbra, Shomy 2025-2026, core/src/da/xflash/cmd.rs.
namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>XFlash wire commands. A catalog entry does not imply device support or authorization.</summary>
/// <remarks>Extension values describe an already loaded compatible ABI; this catalog never loads or patches a DA.</remarks>
public enum MtkXFlashCommand : uint
{
    /// <summary>SyncSignal wire identifier.</summary>
    SyncSignal = 0x434E5953,
    /// <summary>Unknown wire identifier.</summary>
    Unknown = 0x010000,
    /// <summary>Download wire identifier.</summary>
    Download = 0x010001,
    /// <summary>Upload wire identifier.</summary>
    Upload = 0x010002,
    /// <summary>Format wire identifier.</summary>
    Format = 0x010003,
    /// <summary>WriteData wire identifier.</summary>
    WriteData = 0x010004,
    /// <summary>ReadData wire identifier.</summary>
    ReadData = 0x010005,
    /// <summary>FormatPartition wire identifier.</summary>
    FormatPartition = 0x010006,
    /// <summary>Shutdown wire identifier.</summary>
    Shutdown = 0x010007,
    /// <summary>BootTo wire identifier.</summary>
    BootTo = 0x010008,
    /// <summary>DeviceCtrl wire identifier.</summary>
    DeviceCtrl = 0x010009,
    /// <summary>InitExtRam wire identifier.</summary>
    InitExtRam = 0x01000A,
    /// <summary>SwitchUsbSpeed wire identifier.</summary>
    SwitchUsbSpeed = 0x01000B,
    /// <summary>ReadOtpZone wire identifier.</summary>
    ReadOtpZone = 0x01000C,
    /// <summary>WriteOtpZone wire identifier.</summary>
    WriteOtpZone = 0x01000D,
    /// <summary>WriteEfuse wire identifier.</summary>
    WriteEfuse = 0x01000E,
    /// <summary>ReadEfuse wire identifier.</summary>
    ReadEfuse = 0x01000F,
    /// <summary>NandBmtRemark wire identifier.</summary>
    NandBmtRemark = 0x010010,
    /// <summary>SramWriteTest wire identifier.</summary>
    SramWriteTest = 0x010011,
    /// <summary>SetupEnvironment wire identifier.</summary>
    SetupEnvironment = 0x010100,
    /// <summary>SetupHwInitParams wire identifier.</summary>
    SetupHwInitParams = 0x010101,
    /// <summary>SetBmtPercentage wire identifier.</summary>
    SetBmtPercentage = 0x020001,
    /// <summary>SetBatteryOpt wire identifier.</summary>
    SetBatteryOpt = 0x020002,
    /// <summary>SetChecksumLevel wire identifier.</summary>
    SetChecksumLevel = 0x020003,
    /// <summary>SetResetKey wire identifier.</summary>
    SetResetKey = 0x020004,
    /// <summary>SetHostInfo wire identifier.</summary>
    SetHostInfo = 0x020005,
    /// <summary>SetMetaBootMode wire identifier.</summary>
    SetMetaBootMode = 0x020006,
    /// <summary>SetEmmcHwresetPin wire identifier.</summary>
    SetEmmcHwresetPin = 0x020007,
    /// <summary>SetGenerateGpx wire identifier.</summary>
    SetGenerateGpx = 0x020008,
    /// <summary>SetRegisterValue wire identifier.</summary>
    SetRegisterValue = 0x020009,
    /// <summary>SetExternalSig wire identifier.</summary>
    SetExternalSig = 0x02000A,
    /// <summary>SetRemoteSecPolicy wire identifier.</summary>
    SetRemoteSecPolicy = 0x02000B,
    /// <summary>SetAllInOneSig wire identifier.</summary>
    SetAllInOneSig = 0x02000C,
    /// <summary>SetRscInfo wire identifier.</summary>
    SetRscInfo = 0x02000D,
    /// <summary>SetRebootMode wire identifier.</summary>
    SetRebootMode = 0x02000E,
    /// <summary>SetCertFile wire identifier.</summary>
    SetCertFile = 0x02000F,
    /// <summary>SetUpdateFw wire identifier.</summary>
    SetUpdateFw = 0x020010,
    /// <summary>SetUfsConfig wire identifier.</summary>
    SetUfsConfig = 0x020011,
    /// <summary>SetDynamicPartMap wire identifier.</summary>
    SetDynamicPartMap = 0x020012,
    /// <summary>GetEmmcInfo wire identifier.</summary>
    GetEmmcInfo = 0x040001,
    /// <summary>GetNandInfo wire identifier.</summary>
    GetNandInfo = 0x040002,
    /// <summary>GetNorInfo wire identifier.</summary>
    GetNorInfo = 0x040003,
    /// <summary>GetUfsInfo wire identifier.</summary>
    GetUfsInfo = 0x040004,
    /// <summary>GetDaVersion wire identifier.</summary>
    GetDaVersion = 0x040005,
    /// <summary>GetExpireData wire identifier.</summary>
    GetExpireData = 0x040006,
    /// <summary>GetPacketLength wire identifier.</summary>
    GetPacketLength = 0x040007,
    /// <summary>GetRandomId wire identifier.</summary>
    GetRandomId = 0x040008,
    /// <summary>GetPartitionTblCata wire identifier.</summary>
    GetPartitionTblCata = 0x040009,
    /// <summary>GetConnectionAgent wire identifier.</summary>
    GetConnectionAgent = 0x04000A,
    /// <summary>GetUsbSpeed wire identifier.</summary>
    GetUsbSpeed = 0x04000B,
    /// <summary>GetRamInfo wire identifier.</summary>
    GetRamInfo = 0x04000C,
    /// <summary>GetChipId wire identifier.</summary>
    GetChipId = 0x04000D,
    /// <summary>GetOtpLockStatus wire identifier.</summary>
    GetOtpLockStatus = 0x04000E,
    /// <summary>GetBatteryVoltage wire identifier.</summary>
    GetBatteryVoltage = 0x04000F,
    /// <summary>GetRpmbStatus wire identifier.</summary>
    GetRpmbStatus = 0x040010,
    /// <summary>GetExpireDate wire identifier.</summary>
    GetExpireDate = 0x040011,
    /// <summary>GetDramType wire identifier.</summary>
    GetDramType = 0x040012,
    /// <summary>GetDevFwInfo wire identifier.</summary>
    GetDevFwInfo = 0x040013,
    /// <summary>GetHrid wire identifier.</summary>
    GetHrid = 0x040014,
    /// <summary>GetErrorDetail wire identifier.</summary>
    GetErrorDetail = 0x040015,
    /// <summary>SlaEnabledStatus wire identifier.</summary>
    SlaEnabledStatus = 0x040016,
    /// <summary>StartDlInfo wire identifier.</summary>
    StartDlInfo = 0x080001,
    /// <summary>EndDlInfo wire identifier.</summary>
    EndDlInfo = 0x080002,
    /// <summary>ActLockOtpZone wire identifier.</summary>
    ActLockOtpZone = 0x080003,
    /// <summary>DisableEmmcHwresetPin wire identifier.</summary>
    DisableEmmcHwresetPin = 0x080004,
    /// <summary>CcOptionalDownloadAct wire identifier.</summary>
    CcOptionalDownloadAct = 0x080005,
    /// <summary>DaStorLifeCycleCheck wire identifier.</summary>
    DaStorLifeCycleCheck = 0x080007,
    /// <summary>DisableSparseErase wire identifier.</summary>
    DisableSparseErase = 0x080008,
    /// <summary>UnknownCtrlCode wire identifier.</summary>
    UnknownCtrlCode = 0x0E0000,
    /// <summary>CtrlStorageTest wire identifier.</summary>
    CtrlStorageTest = 0x0E0001,
    /// <summary>CtrlRamTest wire identifier.</summary>
    CtrlRamTest = 0x0E0002,
    /// <summary>DeviceCtrlReadRegister wire identifier.</summary>
    DeviceCtrlReadRegister = 0x0E0003,
    /// <summary>ExtAck wire identifier.</summary>
    ExtAck = 0x0F0000,
    /// <summary>ExtSetupDaCtx wire identifier.</summary>
    ExtSetupDaCtx = 0x0F0001,
    /// <summary>ExtReadMem wire identifier.</summary>
    ExtReadMem = 0x0F0002,
    /// <summary>ExtWriteMem wire identifier.</summary>
    ExtWriteMem = 0x0F0003,
    /// <summary>ExtReadRegister wire identifier.</summary>
    ExtReadRegister = 0x0F0004,
    /// <summary>ExtWriteRegister wire identifier.</summary>
    ExtWriteRegister = 0x0F0005,
    /// <summary>ExtKeyDerive wire identifier.</summary>
    ExtKeyDerive = 0x0F0006,
    /// <summary>ExtSej wire identifier.</summary>
    ExtSej = 0x0F0007,
    /// <summary>ExtRpmbInit wire identifier.</summary>
    ExtRpmbInit = 0x0F0008,
    /// <summary>ExtRpmbRead wire identifier.</summary>
    ExtRpmbRead = 0x0F0009,
    /// <summary>ExtRpmbWrite wire identifier.</summary>
    ExtRpmbWrite = 0x0F000A,
}
