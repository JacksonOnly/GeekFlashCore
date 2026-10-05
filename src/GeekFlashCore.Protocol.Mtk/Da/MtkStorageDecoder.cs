namespace GeekFlashCore.Protocol.Mtk.Da;

internal static class MtkStorageDecoder
{
    public static MtkStorageInfo Emmc(ReadOnlySpan<byte> data, bool legacy = false)
    {
        int start = legacy ? 4 : 8;
        if (data.Length < start + 64)
            throw new MtkResourceException("eMMC info");
        uint status = legacy ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (legacy ? status != 0 : status is not (1 or 2))
            throw new MtkResourceException("eMMC type");
        int block = legacy ? 512 : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        if (!legacy && status == 2)
            return Single(MtkStorageKind.Sdmmc, BinaryPrimitives.ReadUInt64LittleEndian(data[64..]), block);
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
    public static MtkStorageInfo Single(MtkStorageKind kind, ulong length, int blockSize, int eraseSize = 0, bool writable = true)
    {
        var region = new MtkStorageRegion(kind, 8, kind switch {
            MtkStorageKind.Nor => "NOR", MtkStorageKind.Nand => "NAND", _ => "SDMMC-USER" }, length, blockSize)
        { CanWrite = writable, EraseBlockSize = eraseSize == 0 && kind == MtkStorageKind.Sdmmc ? blockSize : eraseSize };
        return new(kind, Array.AsReadOnly(new[] { region }), 8, 0);
    }
    public static MtkStorageInfo Nand(ReadOnlySpan<byte> data, bool writable)
    {
        if (data.Length < 45) throw new MtkResourceException("NAND info");
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint page = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint erase = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint spare = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        ulong total = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]), available = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]);
        if (type == 0 || page is < 512 or > 65536 || (page & (page - 1)) != 0 ||
            erase < page || erase > 16777216 || erase % page != 0 || spare > page || data[32] > 1 ||
            available == 0 || available > total || total > long.MaxValue || total % page != 0 || available % erase != 0)
            throw new MtkResourceException("NAND geometry");
        return Single(MtkStorageKind.Nand, available, (int)page, (int)erase, writable) with {
            Nand = new(type, (int)page, (int)spare, (int)erase, total, available, data[32] == 1) };
    }
    public static MtkStorageInfo Nor(ReadOnlySpan<byte> data, int eraseSize)
    {
        if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) == 0) throw new MtkResourceException("NOR info");
        uint page = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        ulong available = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
        if (page is 0 or > 65536 || (page & (page - 1)) != 0 || eraseSize != 0 &&
            (eraseSize < page || eraseSize % page != 0 || available % (uint)eraseSize != 0)) throw new MtkResourceException("NOR geometry");
        return Single(MtkStorageKind.Nor, available, (int)page, eraseSize);
    }
}
