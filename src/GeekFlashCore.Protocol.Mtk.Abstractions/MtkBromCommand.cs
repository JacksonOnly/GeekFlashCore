// SPDX-License-Identifier: AGPL-3.0-or-later
// Command catalog: B. Kerler, mtkclient/Library/mtk_preloader.py, 2018-2024, GPLv3.
namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Complete command catalog from mtkclient Preloader.Cmd. A definition does not imply device support.</summary>
public enum MtkBromCommand : byte
{
    SendPartitionData = 0x70,
    JumpToPartition = 0x71,
    CheckUsbCommand = 0x72,
    StayStill = 0x80,
    Command88 = 0x88,
    Read16A2 = 0xa2,
    I2cInitialize = 0xb0,
    I2cDeinitialize = 0xb1,
    I2cWrite8 = 0xb2,
    I2cRead8 = 0xb3,
    I2cSetSpeed = 0xb4,
    I2cInitializeExtended = 0xb6,
    I2cDeinitializeExtended = 0xb7,
    I2cWrite8Extended = 0xb8,
    I2cRead8Extended = 0xb9,
    I2cSetSpeedExtended = 0xba,
    GetMauiFirmwareVersion = 0xbf,
    OldSlaSendAuthentication = 0xc1,
    OldSlaGetRandomNumber = 0xc2,
    OldSlaVerifyRandomNumber = 0xc3,
    PowerInitialize = 0xc4,
    PowerDeinitialize = 0xc5,
    PowerRead16 = 0xc6,
    PowerWrite16 = 0xc7,
    CacheControl = 0xc8,
    Read16 = 0xd0,
    Read32 = 0xd1,
    Write16 = 0xd2,
    Write16WithoutEcho = 0xd3,
    Write32 = 0xd4,
    JumpDownloadAgent = 0xd5,
    JumpBootLoader = 0xd6,
    SendDownloadAgent = 0xd7,
    GetTargetConfiguration = 0xd8,
    SendEnvironmentPrepare = 0xd9,
    RegisterAccess = 0xda,
    EnableUart1Log = 0xdb,
    SetUart1BaudRate = 0xdc,
    GetBromLog = 0xdd,
    JumpDownloadAgent64 = 0xde,
    GetBromLogNew = 0xdf,
    SendCertificate = 0xe0,
    GetMeId = 0xe1,
    SendAuthentication = 0xe2,
    SerialLinkAuthentication = 0xe3,
    CommandE4 = 0xe4,
    CommandE5 = 0xe5,
    CommandE6 = 0xe6,
    GetSocId = 0xe7,
    CommandE8 = 0xe8,
    Zeroization = 0xf0,
    GetPreloaderCapabilities = 0xfb,
    CommandFa = 0xfa,
    GetHardwareSoftwareVersion = 0xfc,
    GetHardwareCode = 0xfd,
    GetBootLoaderVersion = 0xfe,
    GetBromVersion = 0xff
}

/// <summary>Standard single-byte BROM/Preloader response values.</summary>
public enum MtkBromResponse : byte
{
    Continue = 0x69, Stop = 0x96, Ack = 0x5a, Nak = 0xa5
}

/// <summary>Defined bits in the first Preloader capability word.</summary>
[Flags]
public enum MtkPreloaderCapability : uint
{
    None = 0, XFlash = 1, MeId = 2, SocId = 4
}

/// <summary>The two big-endian halfwords returned by GET_HW_CODE; the second is not a status.</summary>
public readonly record struct MtkBromHardwareCode(ushort Code, ushort Version);
/// <summary>The three version halfwords returned by GET_HW_SW_VER before its status.</summary>
public readonly record struct MtkBromHardwareSoftwareVersion(ushort SubCode, ushort HardwareVersion, ushort SoftwareVersion);
/// <summary>Both raw capability words are preserved, including unknown bits.</summary>
public readonly record struct MtkPreloaderCapabilities(uint Raw0, uint Raw1)
{
    public MtkPreloaderCapability Flags => (MtkPreloaderCapability)Raw0;
}
/// <summary>Raw response of the documented C8/B1 exchange; it is not evidence of an exploit or a completed jump.</summary>
public readonly record struct MtkBromCacheResult(byte Response, ushort Status);
