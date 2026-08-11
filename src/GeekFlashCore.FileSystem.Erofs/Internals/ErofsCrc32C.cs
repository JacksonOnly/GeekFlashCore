namespace GeekFlashCore.FileSystem.Erofs.Internals;

internal static class ErofsCrc32C
{
    private const uint Polynomial = 0x82F63B78;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? Polynomial : 0);
        }

        return crc;
    }
}
