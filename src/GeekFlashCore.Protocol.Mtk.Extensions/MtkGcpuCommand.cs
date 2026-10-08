// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard GCPU opcodes/registers: mtkclient hwcrypto_gcpu.py, B. Kerler, GPLv3.
namespace GeekFlashCore.Protocol.Mtk.Extensions;

internal enum MtkGcpuCommand : uint
{
    LoadHardwareKey = 0x70,
    DecryptEcb = 0x78,
    EncryptEcb = 0x79,
    DecryptPacketEcb = 0x7a,
    EncryptPacketEcb = 0x7b,
    DecryptCbc = 0x7c,
    EncryptCbc = 0x7d,
    DecryptCbcWithEncryptedKey = 0x7e
}

internal static class MtkGcpuRegisters
{
    internal const uint Control = 0;
    internal const uint Misc = 4;
    internal const uint Axi = 0x20;
    internal const uint Unknown2 = 0x24;
    internal const uint ProgramCounterControl = 0x400;
    internal const uint MemoryAddress = 0x404;
    internal const uint DramMonitor = 0x418;
    internal const uint InterruptStatus = 0x800;
    internal const uint InterruptClear = 0x804;
    internal const uint InterruptEnable = 0x808;
    internal const uint Unknown3 = 0x80c;
    internal const uint MemoryCommand = 0xc00;
    internal const uint Parameter0 = 0xc04;
    internal const uint Parameter1 = 0xc08;
    internal const uint Parameter2 = 0xc0c;
    internal const uint Parameter3 = 0xc10;
    internal const uint Parameter4 = 0xc14;
    internal const uint Parameter5 = 0xc18;
    internal const uint Parameter6 = 0xc1c;
    internal const uint OutputSlot = MemoryCommand + 0x1a * sizeof(uint);
}
