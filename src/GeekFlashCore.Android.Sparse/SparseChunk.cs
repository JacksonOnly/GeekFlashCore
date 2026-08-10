using System.Runtime.InteropServices;
using GeekFlashCore.Android.Sparse.Types;

namespace GeekFlashCore.Android.Sparse;


[StructLayout(LayoutKind.Sequential)]
public readonly struct SparseChunk
{
    private readonly ulong _metadata;

    internal SparseChunk(
        long outputOffset,
        long outputLength,
        long payloadOffset,
        SparseChunkType type,
        uint value)
    {
        OutputOffset = outputOffset;
        OutputLength = outputLength;
        PayloadOffset = payloadOffset;
        Type = type;
        _metadata = value;
    }

    public long OutputOffset { get; }
    public long OutputLength { get; }
    public long PayloadOffset { get; }
    public SparseChunkType Type { get; }
    public uint PayloadLength => Type switch
    {
        SparseChunkType.Raw => checked((uint)OutputLength),
        SparseChunkType.Fill or SparseChunkType.Crc32 => sizeof(uint),
        _ => 0
    };
    public uint FillValue => Type == SparseChunkType.Fill ? (uint)_metadata : 0;
    public uint ExpectedCrc32 => Type == SparseChunkType.Crc32 ? (uint)_metadata : 0;
}
