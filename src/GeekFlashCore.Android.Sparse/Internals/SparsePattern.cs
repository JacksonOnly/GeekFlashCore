using System.Buffers.Binary;

namespace GeekFlashCore.Android.Sparse.Internals;

internal static class SparsePattern
{
    public static void Fill(Span<byte> destination, uint pattern, long patternOffset)
    {
        if (pattern == 0)
        {
            destination.Clear();
            return;
        }

        Span<byte> patternBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(patternBytes, pattern);

        int phase = (int)(patternOffset & (sizeof(uint) - 1));
        int prefixLength = Math.Min(destination.Length, sizeof(uint) - phase);
        patternBytes.Slice(phase, prefixLength).CopyTo(destination);
        int filledLength = prefixLength;

        if (filledLength == destination.Length)
            return;

        int seedLength = Math.Min(sizeof(uint), destination.Length - filledLength);
        patternBytes[..seedLength].CopyTo(destination[filledLength..]);
        filledLength += seedLength;

        while (filledLength < destination.Length)
        {
            int copyLength = Math.Min(
                filledLength - prefixLength,
                destination.Length - filledLength);
            destination.Slice(prefixLength, copyLength).CopyTo(destination[filledLength..]);
            filledLength += copyLength;
        }
    }
}
