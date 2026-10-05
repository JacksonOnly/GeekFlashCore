namespace GeekFlashCore.Protocol.Mtk.Da;

internal static class MtkStorageDecoder
{
    public static MtkStorageInfo Emmc(ReadOnlySpan<byte> data, bool legacy = false)
    {
        int start = legacy ? 4 : 8;
        if (data.Length < start + 64)
            throw new MtkResourceException("eMMC info");
        uint status = legacy ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (legacy ? status != 0 : status != 1)
            throw new MtkResourceException("eMMC type");
        int block = legacy ? 512 : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        List<MtkStorageRegion> regions = [];
        ulong rpmb = 0;
        for (uint id = 1; id <= 8; id++)
        {
            ReadOnlySpan<byte> sizeBytes = data.Slice(start + ((int)id - 1) * 8, 8);
            ulong size = legacy ? BinaryPrimitives.ReadUInt64BigEndian(sizeBytes) : BinaryPrimitives.ReadUInt64LittleEndian(sizeBytes);
            if (id == 3)
            {
                rpmb = size;
                continue;
            }
            if (size != 0)
                regions.Add(new(MtkStorageKind.Emmc, id, id switch
                {
                    1 => "EMMC-BOOT1",
                    2 => "EMMC-BOOT2",
                    8 => "EMMC-USER",
                    _ => $"EMMC-GP{id - 3}"
                }, size, block));
        }
        if (!regions.Any(r => r.WireId == 8) || rpmb % 256 != 0 || rpmb / 256 > uint.MaxValue)
            throw new MtkResourceException("eMMC geometry");
        return new(MtkStorageKind.Emmc, regions.AsReadOnly(), 8, (uint)(rpmb / 256));
    }
    public static MtkStorageInfo Ufs(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x30)
            throw new MtkResourceException("UFS info");
        int block = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        List<MtkStorageRegion> regions = [];
        for (uint i = 0; i < 3; i++)
        {
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8 + (int)i * 8, 8));
            if (size > 0)
                regions.Add(new(MtkStorageKind.Ufs, i + 1, $"UFS-LUA{i}", size, block));
        }
        if (!regions.Any(r => r.WireId == 3))
            throw new MtkResourceException("UFS geometry");
        // Standard GET_UFS_INFO does not report RPMB capacity; never guess it.
        return new(MtkStorageKind.Ufs, regions.AsReadOnly(), 3, 0);
    }
}
