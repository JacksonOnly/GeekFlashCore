using System.Collections.ObjectModel;
using System.Security.Cryptography;
using GeekFlashCore.Firmware.Internals;
using GeekFlashCore.Firmware.Localization;

namespace GeekFlashCore.Firmware;

/// <summary>A read-only firmware catalog. The input data source is borrowed.</summary>
/// <remarks>Dispose invalidates entries and streams. Callers dispose each opened stream.
/// Nested containers are opened explicitly through FirmwareUnpacker.Open(entry).</remarks>
public sealed class FirmwarePackage : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationToken _cancellation;
    private readonly List<byte[]> _secrets = [];
    private IReadOnlyDictionary<string, FirmwareEntry[]> _index = new Dictionary<string, FirmwareEntry[]>();
    private bool _disposed;
    internal FirmwarePackage(FirmwareFormat format, FirmwareOpenOptions options, CancellationToken cancellation)
    { Format = format; Options = options; _cancellation = cancellation; }
    /// <summary>The detected container format.</summary>
    public FirmwareFormat Format { get; private set; }
    internal void SetFormat(FirmwareFormat format) => Format = format;
    /// <summary>Immutable entries in container order, including generated scripts.</summary>
    public IReadOnlyList<FirmwareEntry> Entries { get; private set; } = Array.Empty<FirmwareEntry>();
    internal FirmwareOpenOptions Options { get; }
    internal void Initialize(List<FirmwareEntry> entries)
    {
        Entries = new ReadOnlyCollection<FirmwareEntry>(entries.ToArray());
        _index = entries.GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }
    internal byte[] Secret(byte[] bytes) { _secrets.Add(bytes); return bytes; }
    internal void Check(CancellationToken ct = default)
    { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this); _cancellation.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested(); }
    internal Stream Open(Func<CancellationToken, Stream> factory, CancellationToken ct)
    {
        lock (_gate)
        {
            Check(ct);
            CancellationTokenSource? linked = null;
            CancellationToken effective = _cancellation.CanBeCanceled ? _cancellation : ct;
            if (_cancellation.CanBeCanceled && ct.CanBeCanceled && _cancellation != ct)
            { linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, ct); effective = linked.Token; }
            Stream? stream = null;
            try { stream = factory(effective); Check(ct); return new PackageStream(this, stream, effective, linked); }
            catch { try { stream?.Dispose(); } finally { linked?.Dispose(); } throw; }
        }
    }
    /// <summary>Gets an exact, case-sensitive normalized path; duplicate paths are rejected.</summary>
    public FirmwareEntry GetEntry(string name)
    {
        Check(); name = FirmwarePath.Normalize(name);
        if (!_index.TryGetValue(name, out var matches)) throw new FileNotFoundException(Strings.EntryMissing, name);
        if (matches.Length != 1) throw new InvalidDataException(Strings.AmbiguousEntry);
        return matches[0];
    }
    /// <summary>Resolves a relative image name against a script inside this package.</summary>
    public FirmwareEntry ResolveEntry(string scriptName, string relativeName)
    {
        Check(); string script = FirmwarePath.Normalize(scriptName), relative = FirmwarePath.Normalize(relativeName);
        int slash = script.LastIndexOf('/');
        return GetEntry(slash < 0 ? relative : script[..(slash + 1)] + relative);
    }
    /// <summary>Invalidates this catalog and clears internal cryptographic material.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            foreach (var key in _secrets) CryptographicOperations.ZeroMemory(key);
            _secrets.Clear();
        }
    }
}
