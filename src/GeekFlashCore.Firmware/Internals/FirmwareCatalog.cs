using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware.Internals;

internal class FirmwareCatalog(FirmwarePackage package, CancellationToken cancellation)
{
    internal FirmwarePackage Package { get; } = package;
    internal FirmwareOpenOptions Options => Package.Options;
    internal CancellationToken Cancellation { get; } = cancellation;
    internal List<FirmwareEntry> Entries { get; } = [];
    internal HashSet<string> GeneratedScripts { get; } = new(StringComparer.Ordinal);
    private long _nameBytes;
    internal void Check() => Package.Check();
    internal void Limit(long count, long maximum)
    { Check(); if (count < 0 || count > maximum) throw new InvalidDataException(Strings.InvalidMetadata); }
    internal void Add(string name, long length, Func<CancellationToken, Stream> open)
        => AddCore(name, length, open, null);
    internal void Deferred(string name, Func<CancellationToken, long> length, Func<CancellationToken, Stream> open)
        => AddCore(name, -1, open, length);
    private void AddCore(string name, long length, Func<CancellationToken, Stream> open, Func<CancellationToken, long>? resolve)
    {
        Limit(Entries.Count + 1, Options.MaximumEntries); if (length < 0 && resolve is null) throw new InvalidDataException(Strings.InvalidRange);
        name = FirmwarePath.Normalize(name); _nameBytes = checked(_nameBytes + name.Length * 2L); Limit(_nameBytes, Options.MaximumMetadataBytes);
        Entries.Add(new FirmwareEntry(Package, Entries.Count, name, length, open, resolve));
    }
    internal void Virtual(string name, byte[] bytes) => Add(name, bytes.Length, _ => new MemoryStream(bytes, false));
}
