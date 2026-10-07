using System.Buffers.Binary;
using System.Security.Cryptography;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal sealed class LpImageMetadataSource(LpGeometry geometry, byte[] geometryBlock, byte[] metadata) : IReadableBlockDevice
{
    public BlockDeviceId Id { get; } = new("memory:lp-super-metadata");
    public long Length { get; } = RoundUp(checked(12288L + 2L * geometry.MetadataMaxSize * geometry.MetadataSlotCount), geometry.LogicalBlockSize);
    public int LogicalBlockSize => 512;
    internal byte[] GeometryBlock => geometryBlock;
    internal byte[] Metadata => metadata;
    internal static long RoundUp(long value, uint alignment) => checked(value + (alignment - value % alignment) % alignment);
    internal static byte[] EncodeGeometry(LpGeometry geometry)
    {
        byte[] block = new byte[4096]; BinaryPrimitives.WriteUInt32LittleEndian(block, LpFormat.GeometryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 52);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(40), geometry.MetadataMaxSize);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(44), geometry.MetadataSlotCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(48), geometry.LogicalBlockSize);
        SHA256.HashData(block.AsSpan(0, 52), block.AsSpan(8, 32)); return block;
    }
    public int ReadAt(long offset, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset); if (offset >= Length || destination.IsEmpty) return 0;
        int n = (int)Math.Min(destination.Length, Length - offset); var output = destination[..n]; output.Clear();
        Copy(offset, output, 4096, geometryBlock); Copy(offset, output, 8192, geometryBlock);
        for (int slot = 0; slot < geometry.MetadataSlotCount; slot++)
        {
            Copy(offset, output, LpMetadataSet.GetMetadataOffset(geometry, slot, LpMetadataCopyKind.Primary), metadata);
            Copy(offset, output, LpMetadataSet.GetMetadataOffset(geometry, slot, LpMetadataCopyKind.Backup), metadata);
        }
        return n;

    }
    private static void Copy(long offset, Span<byte> output, long start, byte[] data)
    {
        long first = Math.Max(offset, start), end = Math.Min(offset + output.Length, start + data.Length);
        if (first < end) data.AsSpan((int)(first - start), (int)(end - first)).CopyTo(output.Slice((int)(first - offset), (int)(end - first)));
    }
    public void Dispose() { }
    internal Stream OpenStream(CancellationToken ct) => new MetadataStream(this, ct);
    private sealed class MetadataStream(LpImageMetadataSource source, CancellationToken ct) : Stream
    {
        private long _position; private bool _disposed;
        private void Check() { ObjectDisposedException.ThrowIf(_disposed, this); ct.ThrowIfCancellationRequested(); }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length { get { Check(); return source.Length; } }
        public override long Position { get { Check(); return _position; } set => Seek(value, SeekOrigin.Begin); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Check(); long next = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_position + offset), SeekOrigin.End => checked(Length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            if (next < 0 || next > Length) throw new IOException(Resources.SuperImageInvalid); return _position = next;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) { Check(); int n = source.ReadAt(_position, buffer); _position += n; return n; }
        public override void Flush() => Check();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Resources.SuperImageInvalid);
        public override void SetLength(long value) => throw new NotSupportedException(Resources.SuperImageInvalid);
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    }
}
