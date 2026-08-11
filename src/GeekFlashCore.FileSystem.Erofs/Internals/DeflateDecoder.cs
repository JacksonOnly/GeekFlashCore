using System.IO.Compression;

namespace GeekFlashCore.FileSystem.Erofs.Internals;

internal static class DeflateDecoder
{
    public static bool TryDecode(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        bool allowPartialOutput)
    {
        try
        {
            using var inputStream = new MemoryStream(input.ToArray(), writable: false);
            using var decoder = new DeflateStream(
                inputStream,
                CompressionMode.Decompress,
                leaveOpen: false);

            int written = 0;
            while (written < output.Length)
            {
                int read = decoder.Read(output[written..]);
                if (read == 0) return false;
                written += read;
            }

            return allowPartialOutput || decoder.ReadByte() == -1;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
