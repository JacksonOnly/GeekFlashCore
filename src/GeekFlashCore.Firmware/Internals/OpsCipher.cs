using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace GeekFlashCore.Firmware.Internals;

// OPS feedback transform and tables from FlasherCore.Firmware.
internal static class OpsCipher
{
    internal static readonly uint[] BaseKey = [0x9ee3b5d1, 0x9d04ea5e, 0xabd51d67, 0xafcbafd2];

    internal static readonly byte[] MBox4 =
    [
        0xC4,
        0x5D,
        0x05,
        0x71,
        0x99,
        0xDD,
        0xBB,
        0xEE,
        0x29,
        0xA1,
        0x6D,
        0xC7,
        0xAD,
        0xBF,
        0xA4,
        0x3F,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x0a,
        0x00,
    ];
    internal static readonly byte[] MBox5 =
    [
        0x60,
        0x8a,
        0x3f,
        0x2d,
        0x68,
        0x6b,
        0xd4,
        0x23,
        0x51,
        0x0c,
        0xd0,
        0x95,
        0xbb,
        0x40,
        0xe9,
        0x76,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x0a,
        0x00,
    ];
    internal static readonly byte[] MBox6 =
    [
        0xAA,
        0x69,
        0x82,
        0x9E,
        0x5D,
        0xDE,
        0xB1,
        0x3D,
        0x30,
        0xBB,
        0x81,
        0xA3,
        0x46,
        0x65,
        0xa3,
        0xe1,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x0a,
        0x00,
    ];

    public static readonly byte[] SBox = Convert.FromBase64String(
        "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"
    );

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GSbox(uint offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(SBox.AsSpan((int)offset));
    }

    public static void KeyUpdate(Span<uint> iv1, ReadOnlySpan<byte> asbox)
    {
        uint d = iv1[0] ^ asbox[0];
        uint a = iv1[1] ^ asbox[1];
        uint b = iv1[2] ^ asbox[2];
        uint c = iv1[3] ^ asbox[3];

        var e =
            GSbox(((b >> 0x10) & 0xff) * 8 + 2)
            ^ GSbox(((a >> 8) & 0xff) * 8 + 3)
            ^ GSbox((c >> 0x18) * 8 + 1)
            ^ GSbox((d & 0xff) * 8)
            ^ asbox[4];

        var h =
            GSbox(((c >> 0x10) & 0xff) * 8 + 2)
            ^ GSbox(((b >> 8) & 0xff) * 8 + 3)
            ^ GSbox((d >> 0x18) * 8 + 1)
            ^ GSbox((a & 0xff) * 8)
            ^ asbox[5];

        var i =
            GSbox(((d >> 0x10) & 0xff) * 8 + 2)
            ^ GSbox(((c >> 8) & 0xff) * 8 + 3)
            ^ GSbox((a >> 0x18) * 8 + 1)
            ^ GSbox((b & 0xff) * 8)
            ^ asbox[6];

        a =
            GSbox(((d >> 8) & 0xff) * 8 + 3)
            ^ GSbox(((a >> 0x10) & 0xff) * 8 + 2)
            ^ GSbox((b >> 0x18) * 8 + 1)
            ^ GSbox((c & 0xff) * 8)
            ^ asbox[7];

        var g = 8;
        var loopCount = asbox[0x3c] - 2;

        for (int f = 0; f < loopCount; f++)
        {
            d = e >> 0x18;
            uint m = h >> 0x10;
            uint s = h >> 0x18;
            uint z = e >> 0x10;
            uint l = i >> 0x18;
            uint t = e >> 8;

            e =
                GSbox(((i >> 0x10) & 0xff) * 8 + 2)
                ^ GSbox(((h >> 8) & 0xff) * 8 + 3)
                ^ GSbox((a >> 0x18) * 8 + 1)
                ^ GSbox((e & 0xff) * 8)
                ^ asbox[g];
            h =
                GSbox(((a >> 0x10) & 0xff) * 8 + 2)
                ^ GSbox(((i >> 8) & 0xff) * 8 + 3)
                ^ GSbox(d * 8 + 1)
                ^ GSbox((h & 0xff) * 8)
                ^ asbox[g + 1];
            i =
                GSbox((z & 0xff) * 8 + 2)
                ^ GSbox(((a >> 8) & 0xff) * 8 + 3)
                ^ GSbox(s * 8 + 1)
                ^ GSbox((i & 0xff) * 8)
                ^ asbox[g + 2];
            a =
                GSbox((t & 0xff) * 8 + 3)
                ^ GSbox((m & 0xff) * 8 + 2)
                ^ GSbox(l * 8 + 1)
                ^ GSbox((a & 0xff) * 8)
                ^ asbox[g + 3];
            g += 4;
        }

        // Write back to iv1
        iv1[0] =
            (GSbox(((i >> 0x10) & 0xff) * 8) & 0xff0000)
            ^ (GSbox(((h >> 8) & 0xff) * 8 + 1) & 0xff00)
            ^ (GSbox((a >> 0x18) * 8 + 3) & 0xff000000)
            ^ (GSbox((e & 0xff) * 8 + 2) & 0xFF)
            ^ asbox[g];
        iv1[1] =
            (GSbox(((a >> 0x10) & 0xff) * 8) & 0xff0000)
            ^ (GSbox(((i >> 8) & 0xff) * 8 + 1) & 0xff00)
            ^ (GSbox((e >> 0x18) * 8 + 3) & 0xff000000)
            ^ (GSbox((h & 0xff) * 8 + 2) & 0xFF)
            ^ asbox[g + 3];
        iv1[2] =
            (GSbox(((e >> 0x10) & 0xff) * 8) & 0xff0000)
            ^ (GSbox(((a >> 8) & 0xff) * 8 + 1) & 0xff00)
            ^ (GSbox((h >> 0x18) * 8 + 3) & 0xff000000)
            ^ (GSbox((i & 0xff) * 8 + 2) & 0xFF)
            ^ asbox[g + 2];
        iv1[3] =
            (GSbox(((h >> 0x10) & 0xff) * 8) & 0xff0000)
            ^ (GSbox(((e >> 8) & 0xff) * 8 + 1) & 0xff00)
            ^ (GSbox((i >> 0x18) * 8 + 3) & 0xff000000)
            ^ (GSbox((a & 0xff) * 8 + 2) & 0xFF)
            ^ asbox[g + 1];
    }


}
