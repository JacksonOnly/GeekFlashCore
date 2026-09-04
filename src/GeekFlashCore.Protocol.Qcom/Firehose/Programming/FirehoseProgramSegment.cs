using GeekFlashCore.Android.Sparse.Models;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

internal sealed class FirehoseProgramSegment
{
    private readonly SparseRegion? _sparseRegion;

    private FirehoseProgramSegment(long startSector, long sectorCount, long sourceLength, SparseRegion? sparseRegion)
    {
        StartSector = startSector;
        SectorCount = sectorCount;
        SourceLength = sourceLength;
        _sparseRegion = sparseRegion;
    }

    public long StartSector { get; }
    public long SectorCount { get; }
    public long SourceLength { get; }
    public bool IsSparse => _sparseRegion is not null;

    public static FirehoseProgramSegment Raw(long startSector, long sectorCount, long sourceLength) =>
        new(startSector, sectorCount, sourceLength, null);

    public static FirehoseProgramSegment Sparse(long startSector, long sectorCount, SparseRegion region) =>
        new(startSector, sectorCount, region.Length, region);

    public Stream OpenRead(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _sparseRegion?.OpenRead(source, leaveOpen: true) ?? new NonOwningStream(source);
    }

    private sealed class NonOwningStream(Stream source) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => source.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
