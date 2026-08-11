using System.Buffers;
using ZstdSharp;

namespace GeekFlashCore.FileSystem.Erofs.Internals;

internal static class ZstdDecoder
{
    public static bool TryDecode(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        bool allowPartialOutput)
    {
        try
        {
            using var decoder = new Decompressor();
            if (!allowPartialOutput)
            {
                return decoder.TryUnwrap(input, output, out int written) &&
                    written == output.Length;
            }

            OperationStatus status = decoder.UnwrapStream(
                input,
                output,
                out _,
                out int partialWritten);
            return partialWritten == output.Length &&
                status is OperationStatus.Done or OperationStatus.DestinationTooSmall;
        }
        catch (ZstdException)
        {
            return false;
        }
    }
}
