using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using GeekFlashCore.Firmware.Internals;
using GeekFlashCore.Firmware.Localization;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Firmware;

/// <summary>Opens firmware containers as catalogs of streaming plaintext data sources.</summary>
public static class FirmwareUnpacker
{
    /// <summary>Opens a local firmware container or unpacked directory as a read-only catalog.</summary>
    /// <remarks>The catalog owns its internal resources; callers dispose each opened entry stream.</remarks>
    public static FirmwarePackage Open(string path, FirmwareOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        if (System.IO.Directory.Exists(path)) return OpenDirectory(path, options, cancellationToken);
        return Open(new PathSource(path), options, cancellationToken);
    }
    private static FirmwarePackage OpenDirectory(string path, FirmwareOpenOptions? options, CancellationToken ct)
    {
        options ??= new(); options.Validate(); ct.ThrowIfCancellationRequested();
        if (options.Format is not (FirmwareFormat.Auto or FirmwareFormat.Directory)) throw new NotSupportedException(Strings.Unsupported);
        var package = new FirmwarePackage(FirmwareFormat.Directory, options, ct);
        try
        {
            var catalog = new FirmwareCatalog(package, ct);
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (string file in System.IO.Directory.EnumerateFiles(path, "*", enumeration))
            {
                catalog.Check(); var source = new PathSource(file);
                catalog.Add(Path.GetRelativePath(path, file), source.Length, token => SourceStream.Slice(source, 0, source.Length, token));
            }
            OfpSuperMapper.Directory(catalog); package.Initialize(catalog.Entries); return package;
        }
        catch { package.Dispose(); throw; }
    }
    /// <summary>Parses bounded metadata from a borrowed, stable, seekable, reopenable source.</summary>
    /// <remarks>Keep the returned package alive while Qcom consumes its entries. No extraction directory is created.</remarks>
    public static FirmwarePackage Open(IDataSource source, FirmwareOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); options ??= new(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        using Stream input = new DecoderInputStream(SourceStream.Open(source), cancellationToken);
        FirmwareFormat format = options.Format == FirmwareFormat.Auto ? Detect(input, options) : options.Format;
        var package = new FirmwarePackage(format, options, cancellationToken);
        try
        {
            var context = new ParseContext(new BorrowedSource(source, input.Length), input, package, cancellationToken);
            switch (format)
            {
                case FirmwareFormat.Zip: case FirmwareFormat.Ozip: ZipParser.Parse(context); break;
                case FirmwareFormat.OfpQualcomm: OfpParser.Qualcomm(context); break;
                case FirmwareFormat.OfpMediaTek: OfpParser.MediaTek(context); break;
                case FirmwareFormat.Ops: OpsParser.Parse(context); break;
                case FirmwareFormat.Pac: FlatParsers.Pac(context); break;
                case FirmwareFormat.Kdz: FlatParsers.Kdz(context); break;
                case FirmwareFormat.Dz: FlatParsers.Dz(context); break;
                case FirmwareFormat.UpdateApp: FlatParsers.UpdateApp(context); break;
                case FirmwareFormat.AndroidPayload: PayloadParser.Parse(context); break;
                default: throw new NotSupportedException(Strings.Unsupported);
            }
            cancellationToken.ThrowIfCancellationRequested(); package.Initialize(context.Entries); return package;
        }
        catch (Exception e) when (e is EndOfStreamException or OverflowException or XmlException or CryptographicException or DecoderFallbackException)
        { package.Dispose(); throw new InvalidDataException(Strings.InvalidMetadata, e); }
        catch { package.Dispose(); throw; }
    }
    private static FirmwareFormat Detect(Stream s, FirmwareOpenOptions options)
    {
        Span<byte> prefix = stackalloc byte[12]; int n = s.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        if (n >= 4 && prefix[..4].SequenceEqual("CrAU"u8)) return FirmwareFormat.AndroidPayload;
        if (n == 12 && prefix.SequenceEqual("OPPOENCRYPT!"u8)) return FirmwareFormat.Ozip;
        if (n >= 4 && (prefix[..4].SequenceEqual("PK\x03\x04"u8) || prefix[..4].SequenceEqual("PK\x05\x06"u8))) return FirmwareFormat.Zip;
        if (n >= 8 && BinaryPrimitives.ReadUInt64LittleEndian(prefix) is 0x8025313400000528 or 0x5044793200000518 or 0x2522382400000528) return FirmwareFormat.Kdz;
        if (n >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(prefix) == 0x74189632) return FirmwareFormat.Dz;
        if (s.Length >= 2124 && ReadU32(s, 2116) == 0xFFFAFFFA) return FirmwareFormat.Pac;
        if (s.Length >= 190 && ReadU32(s, 92) == 0xA55AAA55) return FirmwareFormat.UpdateApp;
        foreach (int page in new[] { 4096, 512 })
            if (s.Length >= page && ReadU32(s, s.Length - page + 16) == 0x7CEF) return FirmwareFormat.OfpQualcomm;
        if (OfpParser.IsMediaTek(s)) return FirmwareFormat.OfpMediaTek;
        if (OpsParser.IsOps(s, options)) return FirmwareFormat.Ops;
        throw new NotSupportedException(Strings.Unsupported);
    }
    private static uint ReadU32(Stream s, long offset)
    { Span<byte> b = stackalloc byte[4]; s.Position = offset; s.ReadExactly(b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    private sealed class PathSource(string path) : IDataSource
    {
        public long Length { get; } = new FileInfo(path).Length;
        public Stream OpenStream() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
        public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(OpenStream()); }
    }
    private sealed class BorrowedSource(IDataSource source, long length) : IDataSource
    {
        public long Length => length;
        public Stream OpenStream() => source.OpenStream();
        public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(OpenStream()); }
    }
}
