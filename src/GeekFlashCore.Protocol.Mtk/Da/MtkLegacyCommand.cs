// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard Legacy command values: mtkclient, B. Kerler, dalegacy_lib.py, GPLv3.
namespace GeekFlashCore.Protocol.Mtk.Da;

internal enum MtkLegacyCommand : byte
{
    SwitchPartition = 0x60,
    WriteData = 0x62,
    GetUsbSpeed = 0x72,
    ReadRegister = 0x7a,
    WriteRegister = 0x7b,
    ReadPartitionTable = 0xa5,
    SetSpeed = 0xd2,
    Format = 0xd4,
    ReadData = 0xd6,
    Shutdown = 0xd9,
    ReadNand = 0xdf,
    InitExtRam = 0xe8,
    GetFatInfo = 0xf0
}

internal enum MtkLegacyResponse : byte
{
    Ack = 0x5a,
    Continue = 0x69,
    Nak = 0xa5,
    Sync = 0xc0,
    SocOk = 0xc1
}
