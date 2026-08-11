using System.Buffers.Binary;
using SharpCompress.Compressors.LZMA;

namespace GeekFlashCore.FileSystem.Erofs.Internals;

internal static class MicroLzmaDecoder
{
    private const int MinimumDictionarySize = 4096;

    public static bool TryDecode(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        bool allowPartialOutput)
    {
        if (input.IsEmpty) return false;

        byte property = unchecked((byte)~input[0]);
        if (!IsValidProperty(property)) return false;

        byte[] properties = new byte[5];
        properties[0] = property;
        BinaryPrimitives.WriteInt32LittleEndian(
            properties.AsSpan(1),
            Math.Max(MinimumDictionarySize, output.Length));

        byte[] payload = input.ToArray();
        payload[0] = 0;
        byte[] decoded = new byte[output.Length];
        long expectedOutputLength = allowPartialOutput
            ? checked((long)output.Length + 1)
            : output.Length;

        try
        {
            using var inputStream = new MemoryStream(payload, writable: false);
            using LzmaStream decoder = LzmaStream.Create(properties, inputStream, payload.Length, expectedOutputLength);
            int written = 0;
            while (written < decoded.Length)
            {
                int read = decoder.Read(decoded, written, decoded.Length - written);
                if (read == 0) return false;
                written += read;
            }

            if (!allowPartialOutput && decoder.ReadByte() != -1)
                return false;

            decoded.CopyTo(output);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or EndOfStreamException or ArgumentException ||
            exception.GetType().Namespace == typeof(LzmaStream).Namespace)
        {
            return false;
        }
    }

    private static bool IsValidProperty(byte property)
    {
        if (property > (4 * 5 + 4) * 9 + 8) return false;
        int positionBits = property / (9 * 5);
        int remainder = property - positionBits * 9 * 5;
        int literalPositionBits = remainder / 9;
        int literalContextBits = remainder - literalPositionBits * 9;
        return literalContextBits + literalPositionBits <= 4;
    }
}