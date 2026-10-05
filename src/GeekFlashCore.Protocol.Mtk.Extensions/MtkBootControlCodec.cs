// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Shomy (reference layout)
// Android boot-control layout cross-checked with penumbra/core/src/core/bootctrl.rs.
using System.Buffers.Binary;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Bounded Android boot-control parsing and slot updates; preserves all reserved fields.</summary>
public static class MtkBootControlCodec
{
    public const int MetadataOffset = 0x800;
    public const int MetadataSize = 32;
    /// <summary>Requires a supported version, suffix, slot count and valid CRC32.</summary>
    public static MtkBootControlInfo Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MetadataSize || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 0x42414342 ||
            data[8] != 1 || (data[9] & 7) is < 1 or > 4 || data[0] != '_' || data[2] != 0 || data[3] != 0 ||
            data[1] < 'a' || data[1] >= 'a' + (data[9] & 7) ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[28..]) != Crc32Helper.Compute(data[..28]))
            throw new MtkResourceException("boot control metadata/CRC");
        List<MtkBootSlot> slots = [];
        for (int i = 0; i < (data[9] & 7); i++)
        {
            byte status = data[12 + i * 2];
            slots.Add(new(i, status & 15, (status >> 4) & 7, (status & 128) != 0, (data[13 + i * 2] & 1) != 0));
        }
        int active = slots.OrderByDescending(s => s.Priority).ThenBy(s => s.Index).First().Index;
        return new(data[8], active, slots.AsReadOnly()) { CurrentSlot = data[1] - 'a' };
    }
    /// <summary>Returns only the 32-byte metadata; the containing sector is owned by the caller.</summary>
    public static byte[] SetActiveSlot(ReadOnlySpan<byte> data, int slot)
    {
        var info = Parse(data);
        if (slot < 0 || slot >= info.Slots.Count) throw new ArgumentOutOfRangeException(nameof(slot));
        byte[] result = data[..MetadataSize].ToArray();
        result[1] = (byte)('a' + slot);
        for (int i = 0; i < info.Slots.Count; i++)
            if (i != slot && (result[12 + 2 * i] & 15) == 15)
                result[12 + 2 * i] = (byte)((result[12 + 2 * i] & 0xf0) | 14);
        result[12 + 2 * slot] = 0x7f; // Priority 15, seven tries, successful flag cleared.
        result[13 + 2 * slot] &= 0xfe;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), Crc32Helper.Compute(result.AsSpan(0, 28)));
        return result;
    }
}
