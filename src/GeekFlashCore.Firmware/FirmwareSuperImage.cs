using GeekFlashCore.Android.Lp;
using GeekFlashCore.Firmware.Internals;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Firmware;

/// <summary>A virtual Sparse Super assembled from an explicitly selected Oplus loose-package definition.</summary>
/// <remarks>The parent package is borrowed and must remain alive. Each opened stream is owned by its caller.
/// Construction preflights metadata and Sparse chunk indexes; payload is never expanded or extracted.</remarks>
public sealed class FirmwareSuperImage : IDataSource
{
    private readonly FirmwareEntry _entry;
    private FirmwareSuperImage(FirmwareEntry entry, LpSuperImageLayout layout, string definitionName)
    { _entry = entry; Layout = layout; DefinitionName = definitionName; }
    /// <summary>The explicitly selected package-relative JSON definition.</summary>
    public string DefinitionName { get; }
    /// <summary>The virtual image name, normally IMAGES/super.img.</summary>
    public string Name => _entry.Name;
    /// <summary>Exact Sparse encoded length in bytes.</summary>
    public long Length => _entry.Length;
    /// <summary>Logical Super capacity, independent of encoded size.</summary>
    public long LogicalLength => Layout.Length;
    /// <summary>Validated LP geometry, flags, groups, partitions and extents.</summary>
    public LpSuperImageLayout Layout { get; }
    /// <summary>Preflights a selected super_def JSON and creates a streaming IDataSource over its original images.</summary>
    public static FirmwareSuperImage Open(FirmwarePackage package, string definitionName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package); package.Check(cancellationToken); definitionName = FirmwarePath.Normalize(definitionName);
        var definition = OplusSuperDefinition.Parse(package, definitionName, cancellationToken);
        return Create(package, definition, definitionName, cancellationToken);
    }
    internal static FirmwareSuperImage Create(FirmwarePackage package, OplusSuperDefinition definition, string definitionName, CancellationToken cancellationToken)
    {
        var composition = definition.Layout.CreateSparseImage(definition.Images, new()
        {
            MaximumSources = 128, MaximumOpenSources = 2, MaximumChunks = package.Options.MaximumSegments,
            MaximumMetadataBytes = package.Options.MaximumMetadataBytes
        }, cancellationToken);
        package.Check(cancellationToken);
        var entry = new FirmwareEntry(package, -1, definition.ImageName, composition.EncodedLength, ct => composition.OpenStream(ct));
        return new(entry, definition.Layout, definitionName);
    }
    /// <summary>Opens an independent caller-owned Sparse stream.</summary>
    public Stream OpenStream() => _entry.OpenStream();
    /// <summary>Opens an independent Sparse stream observing package and caller cancellation.</summary>
    public Stream OpenStream(CancellationToken cancellationToken) => _entry.OpenStream(cancellationToken);
    /// <summary>Adapts the synchronous offline source to the framework contract.</summary>
    public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) => ValueTask.FromResult(OpenStream(ct));
}
