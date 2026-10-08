// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard command names: penumbra, Shomy 2025-2026, core/src/da/xml/cmd.rs.
namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Allowlisted XML command names without the CMD: prefix; argument validation remains mandatory.</summary>
public static class MtkXmlCommand
{
    /// <summary>Prefix used by the command element on the wire.</summary>
    public const string Prefix = "CMD:";
    /// <summary>SET-RUNTIME-PARAMETER wire command name.</summary>
    public const string SetRuntimeParameter = "SET-RUNTIME-PARAMETER";
    /// <summary>HOST-SUPPORTED-COMMANDS wire command name.</summary>
    public const string HostSupportedCommands = "HOST-SUPPORTED-COMMANDS";
    /// <summary>NOTIFY-INIT-HW wire command name.</summary>
    public const string NotifyInitHw = "NOTIFY-INIT-HW";
    /// <summary>SET-HOST-INFO wire command name.</summary>
    public const string SetHostInfo = "SET-HOST-INFO";
    /// <summary>BOOT-TO wire command name.</summary>
    public const string BootTo = "BOOT-TO";
    /// <summary>GET-HW-INFO wire command name.</summary>
    public const string GetHwInfo = "GET-HW-INFO";
    /// <summary>READ-PARTITION-TABLE wire command name.</summary>
    public const string ReadPartitionTable = "READ-PARTITION-TABLE";
    /// <summary>GET-SYS-PROPERTY wire command name.</summary>
    public const string GetSysProperty = "GET-SYS-PROPERTY";
    /// <summary>READ-REGISTER wire command name.</summary>
    public const string ReadRegister = "READ-REGISTER";
    /// <summary>WRITE-REGISTER wire command name.</summary>
    public const string WriteRegister = "WRITE-REGISTER";
    /// <summary>SECURITY-GET-DEV-FW-INFO wire command name.</summary>
    public const string SecurityGetDevFwInfo = "SECURITY-GET-DEV-FW-INFO";
    /// <summary>SECURITY-SET-FLASH-POLICY wire command name.</summary>
    public const string SecuritySetFlashPolicy = "SECURITY-SET-FLASH-POLICY";
    /// <summary>SECURITY-SET-ALLINONE-SIGNATURE wire command name.</summary>
    public const string SecuritySetAllinoneSignature = "SECURITY-SET-ALLINONE-SIGNATURE";
    /// <summary>READ-EFUSE wire command name.</summary>
    public const string ReadEfuse = "READ-EFUSE";
    /// <summary>WRITE-EFUSE wire command name.</summary>
    public const string WriteEfuse = "WRITE-EFUSE";
    /// <summary>READ-PARTITION wire command name.</summary>
    public const string ReadPartition = "READ-PARTITION";
    /// <summary>WRITE-PARTITION wire command name.</summary>
    public const string WritePartition = "WRITE-PARTITION";
    /// <summary>ERASE-PARTITION wire command name.</summary>
    public const string ErasePartition = "ERASE-PARTITION";
    /// <summary>FLASH-UPDATE wire command name.</summary>
    public const string FlashUpdate = "FLASH-UPDATE";
    /// <summary>READ-FLASH wire command name.</summary>
    public const string ReadFlash = "READ-FLASH";
    /// <summary>WRITE-FLASH wire command name.</summary>
    public const string WriteFlash = "WRITE-FLASH";
    /// <summary>ERASE-FLASH wire command name.</summary>
    public const string EraseFlash = "ERASE-FLASH";
    /// <summary>REBOOT wire command name.</summary>
    public const string Reboot = "REBOOT";
    /// <summary>SET-BOOT-MODE wire command name.</summary>
    public const string SetBootMode = "SET-BOOT-MODE";
    /// <summary>EXT-ACK wire command name.</summary>
    public const string ExtAck = "EXT-ACK";
    /// <summary>EXT-DA-CTX wire command name.</summary>
    public const string ExtDaCtx = "EXT-DA-CTX";
    /// <summary>EXT-READ-MEM wire command name.</summary>
    public const string ExtReadMem = "EXT-READ-MEM";
    /// <summary>EXT-WRITE-MEM wire command name.</summary>
    public const string ExtWriteMem = "EXT-WRITE-MEM";
    /// <summary>EXT-KEY-DERIVE wire command name.</summary>
    public const string ExtKeyDerive = "EXT-KEY-DERIVE";
    /// <summary>EXT-SEJ wire command name.</summary>
    public const string ExtSej = "EXT-SEJ";
    /// <summary>EXT-RPMB-INIT wire command name.</summary>
    public const string ExtRpmbInit = "EXT-RPMB-INIT";
    /// <summary>EXT-RPMB-READ wire command name.</summary>
    public const string ExtRpmbRead = "EXT-RPMB-READ";
    /// <summary>EXT-RPMB-WRITE wire command name.</summary>
    public const string ExtRpmbWrite = "EXT-RPMB-WRITE";
    /// <summary>START wire command name.</summary>
    public const string Start = "START";
    /// <summary>END wire command name.</summary>
    public const string End = "END";
    /// <summary>UPLOAD-FILE wire command name.</summary>
    public const string UploadFile = "UPLOAD-FILE";
    /// <summary>DOWNLOAD-FILE wire command name.</summary>
    public const string DownloadFile = "DOWNLOAD-FILE";
    /// <summary>PROGRESS-REPORT wire command name.</summary>
    public const string ProgressReport = "PROGRESS-REPORT";
    /// <summary>FILE-SYS-OPERATION wire command name.</summary>
    public const string FileSysOperation = "FILE-SYS-OPERATION";
}
