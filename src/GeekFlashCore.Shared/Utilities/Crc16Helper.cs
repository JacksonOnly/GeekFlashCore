namespace GeekFlashCore.Shared.Utilities;

public static class Crc16Helper
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = ushort.MaxValue;
        foreach (byte value in data)
        {
            ushort current = (ushort)(crc ^ value);
            for (int bit = 0; bit < 8; bit++)
            {
                current = (current & 1) != 0
                    ? (ushort)((current >> 1) ^ 0xA001)
                    : (ushort)(current >> 1);
            }

            crc = current;
        }

        return crc;
    }
    public static ushort Append(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            ushort current = (ushort)(crc ^ value);
            for (int bit = 0; bit < 8; bit++)
            {
                current = (current & 1) != 0
                    ? (ushort)((current >> 1) ^ 0xA001)
                    : (ushort)(current >> 1);
            }

            crc = current;
        }

        return crc;
    }
}