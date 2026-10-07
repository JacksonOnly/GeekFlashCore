using System.Buffers.Binary;
using System.Text;
using GeekFlashCore.Firmware.Localization;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Firmware.Internals;

internal sealed class ParseContext(IDataSource source, Stream input, FirmwarePackage package, CancellationToken cancellation)
{
    internal IDataSource Source { get; } = source;
    internal Stream Input { get; } = input;
    internal FirmwarePackage Package { get; } = package;
    internal FirmwareOpenOptions Options => Package.Options;
    internal CancellationToken Cancellation { get; } = cancellation;
    internal List<FirmwareEntry> Entries { get; } = [];
    internal HashSet<string> GeneratedScripts { get; } = new(StringComparer.Ordinal);
    private long _nameBytes;
    internal void Check() => Package.Check();
    internal void Limit(long count, long maximum) { Check(); if (count < 0 || count > maximum) throw new InvalidDataException(Strings.InvalidMetadata); }
    internal byte[] Read(long offset, int size)
    { Limit(size, Options.MaximumMetadataBytes); SourceStream.Range(Source.Length, offset, size); byte[] data = new byte[size]; Input.Position = offset; Input.ReadExactly(data); return data; }
    internal void Add(string name, long length, Func<CancellationToken, Stream> open)
    {
        Limit(Entries.Count + 1, Options.MaximumEntries); if (length < 0) throw new InvalidDataException(Strings.InvalidRange);
        name = FirmwarePath.Normalize(name); _nameBytes = checked(_nameBytes + name.Length * 2L); Limit(_nameBytes, Options.MaximumMetadataBytes);
        Entries.Add(new FirmwareEntry(Package, Entries.Count, name, length, open));
    }
    internal void Slice(string name, long offset, long length)
    { SourceStream.Range(Source.Length, offset, length); Add(name, length, ct => SourceStream.Slice(Source, offset, length, ct)); }
    internal void Virtual(string name, byte[] bytes) => Add(name, bytes.Length, _ => new MemoryStream(bytes, false));
    internal void Encrypted(string name, long offset, long length, long encryptedLength, byte[] key, byte[] iv)
    {
        SourceStream.Range(Source.Length, offset, length); SourceStream.Range(length, 0, encryptedLength);
        Add(name, length, ct => new CfbStream(SourceStream.Slice(Source, offset, length, ct), encryptedLength, key, iv, ct));
    }
    internal static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    internal static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
    internal static long Long(ulong value) => value <= long.MaxValue ? (long)value : throw new InvalidDataException(Strings.InvalidRange);
    internal static string Text(ReadOnlySpan<byte> bytes)
    { int end = bytes.IndexOf((byte)0); return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]); }
    internal static string Unicode(ReadOnlySpan<byte> bytes)
    { int n = 0; while (n + 1 < bytes.Length && (bytes[n] != 0 || bytes[n + 1] != 0)) n += 2; return Encoding.Unicode.GetString(bytes[..n]); }
}
