using GeekFlashCore.Android.Lp;
using GeekFlashCore.Android.Sparse;
using GeekFlashCore.Firmware.Internals;
using GeekFlashCore.Firmware.Localization;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Firmware;

/// <summary>A metadata-first Super write plan that opens partition payload only when it is consumed.</summary>
/// <remarks>The package is borrowed. Planning checks fixed headers, not every chunk; later corruption may
/// be discovered after earlier data has been written. Write stops immediately and never retries.</remarks>
public sealed class FirmwareSuperImagePlan
{
    private readonly FirmwarePackage _package;
    private readonly OplusSuperDefinition _definition;
    private readonly SourceSnapshot[] _sources;
    private FirmwareSuperImagePlan(FirmwarePackage package, OplusSuperDefinition definition, string name, SourceSnapshot[] sources)
    { _package = package; _definition = definition; DefinitionName = name; _sources = sources; }
    /// <summary>The selected package-relative JSON definition.</summary>
    public string DefinitionName { get; }
    /// <summary>The virtual image name.</summary>
    public string Name => _definition.ImageName;
    /// <summary>Logical Super capacity in bytes.</summary>
    public long LogicalLength => Layout.Length;
    /// <summary>Validated geometry, groups, partitions and extents.</summary>
    public LpSuperImageLayout Layout => _definition.Layout;

    /// <summary>Checks LP metadata and partition fixed headers without scanning payload or indexing chunks.</summary>
    public static FirmwareSuperImagePlan Open(FirmwarePackage package, string definitionName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package); package.Check(cancellationToken); definitionName = FirmwarePath.Normalize(definitionName);
        var definition = OplusSuperDefinition.Parse(package, definitionName, cancellationToken);
        if (definition.Images.Count > 127 || definition.Images.Count * 512L > package.Options.MaximumMetadataBytes)
            throw new InvalidDataException(Strings.SuperDefinitionInvalid);
        var sources = new List<SourceSnapshot>(definition.Images.Count); long chunks = 0;
        foreach (var p in definition.Layout.Partitions.Span)
        {
            package.Check(cancellationToken); if (p.LogicalSize == 0) continue;
            var factory = definition.Images[p.RawName]; using var stream = factory(cancellationToken);
            if (!stream.CanRead || !stream.CanSeek || stream.Position != 0 || stream.Length <= 0) throw new InvalidDataException(Strings.SuperDefinitionInvalid);
            SparseHeader? header = SparseSequentialReader.TryReadHeader(stream, package.Options.MaximumSegments, cancellationToken);
            if (header is not null)
            {
                if (header.Value.RawLength != p.LogicalSize || header.Value.BlockSize != definition.Layout.Geometry.LogicalBlockSize)
                    throw new InvalidDataException(Strings.SuperDefinitionInvalid);
                chunks = checked(chunks + header.Value.TotalChunks);
            }
            else { if (stream.Length != p.LogicalSize) throw new InvalidDataException(Strings.SuperDefinitionInvalid); chunks++; }
            if (chunks > package.Options.MaximumSegments) throw new InvalidDataException(Strings.SuperDefinitionInvalid);
            sources.Add(new(p.RawName, p.FirstExtentIndex, p.ExtentCount, factory, stream.Length, header));
        }
        package.Check(cancellationToken); return new(package, definition, definitionName, sources.ToArray());
    }

    /// <summary>Writes metadata, then consumes each original partition once in forward order.</summary>
    /// <remarks>Each callback receives an explicitly raw IDataSource with one caller-owned forward stream.
    /// Consume it completely during the callback. The source and its stream expire on callback return.</remarks>
    /// <returns>The material bytes consumed, excluding DontCare and unallocated ranges.</returns>
    public long Write(Action<FirmwareSuperImageWriteRegion, IDataSource> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write); Check(); long written = 0;
        using (var metadata = Layout.OpenMetadataStream(cancellationToken)) Dispatch("metadata", 0, Layout.MetadataLength, metadata);
        foreach (var source in _sources)
        {
            Check(); using var stream = source.Open(cancellationToken);
            if (!stream.CanRead || !stream.CanSeek || stream.Position != 0 || stream.Length != source.Length) throw new InvalidDataException(Strings.SuperDefinitionInvalid);
            int extentIndex = 0; long extentStart = 0;
            if (source.Header is { } header)
                SparseSequentialReader.ReadData(stream, (region, data) => Map(region.OutputOffset, region.Length, data), header, _package.Options.MaximumSegments, cancellationToken);
            else Map(0, source.Length, stream);
            Check();

            void Map(long offset, long length, Stream data)
            {
                while (length > 0)
                {
                    Check(); if (extentIndex >= source.ExtentCount) throw new InvalidDataException(Strings.SuperDefinitionInvalid);
                    var extent = Layout.Extents.Span[checked((int)source.FirstExtent + extentIndex)]; long extentLength = checked((long)extent.SectorCount * 512);
                    if (offset >= extentStart + extentLength) { extentStart = checked(extentStart + extentLength); extentIndex++; continue; }
                    if (offset < extentStart) throw new InvalidDataException(Strings.SuperDefinitionInvalid);
                    long n = Math.Min(length, extentStart + extentLength - offset);
                    Dispatch(source.Name, checked((long)extent.TargetData * 512 + offset - extentStart), n, data); offset += n; length -= n;
                }
            }
        }
        return written;

        void Check() => _package.Check(cancellationToken);
        void Dispatch(string name, long offset, long length, Stream stream)
        {
            Check(); using var source = new ForwardDataSource(stream, length, Check);
            write(new(name, offset, length), source); Check();
            if (source.Remaining != 0) throw new InvalidDataException(Strings.SuperRegionIncomplete);
            written = checked(written + length);
        }
    }
    /// <summary>Explicitly scans all chunks and returns a seekable virtual Sparse image when required.</summary>
    public FirmwareSuperImage CreateSparseImage(CancellationToken cancellationToken = default)
    { _package.Check(cancellationToken); return FirmwareSuperImage.Create(_package, _definition, DefinitionName, cancellationToken); }

    private sealed record SourceSnapshot(string Name, uint FirstExtent, uint ExtentCount,
        Func<CancellationToken, Stream> Open, long Length, SparseHeader? Header);
    private sealed class ForwardDataSource : IDataSource, IDisposable
    {
        private readonly ForwardStream _stream; private bool _opened, _disposed;
        internal ForwardDataSource(Stream stream, long length, Action check) { Length = length; _stream = new(stream, length, check); }
        public long Length { get; }
        internal long Remaining => _stream.Remaining;
        public Stream OpenStream()
        {
            ObjectDisposedException.ThrowIf(_disposed, this); if (_opened) throw new InvalidOperationException(Strings.SuperSourceAlreadyOpened);
            _opened = true; return _stream;
        }
        public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(OpenStream()); }
        public void Dispose() { _disposed = true; _stream.Dispose(); }
    }
    private sealed class ForwardStream : Stream
    {
        private readonly Stream _source; private readonly long _length; private readonly Action _check; private bool _disposed;
        internal ForwardStream(Stream source, long length, Action check) { _source = source; _length = length; Remaining = length; _check = check; }
        internal long Remaining { get; private set; }
        private void Check() { ObjectDisposedException.ThrowIf(_disposed, this); _check(); }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get { Check(); return _length; } }
        public override long Position { get { Check(); return _length - Remaining; } set => throw new NotSupportedException(Strings.SuperSourceForwardOnly); }
        public override int Read(Span<byte> buffer)
        {
            Check(); int count = (int)Math.Min(Math.Min(buffer.Length, 65536), Remaining); if (count == 0) return 0;
            int n = _source.Read(buffer[..count]); Check(); if (n == 0) throw new EndOfStreamException(Strings.LengthMismatch); Remaining -= n; return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(Strings.SuperSourceForwardOnly);
        public override void Flush() => Check();
        public override void SetLength(long value) => throw new NotSupportedException(Strings.ReadOnly);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(Strings.ReadOnly);
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    }
}

/// <summary>An aligned raw data range relative to the start of the physical Super partition.</summary>
public readonly record struct FirmwareSuperImageWriteRegion(string PartitionName, long OutputOffset, long Length);
