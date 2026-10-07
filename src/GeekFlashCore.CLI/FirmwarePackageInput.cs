using GeekFlashCore.Firmware;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.CLI;

/// <summary>Keeps every containing package alive while its image is consumed by a protocol.</summary>
internal sealed class FirmwarePackageInput : IDisposable
{
    private readonly FirmwarePackageReference? _reference;
    private FirmwarePackageInput(IDataSource? source, string name, FirmwarePackageReference? reference = null, FirmwareSuperImagePlan? plan = null)
    { Source = source; Name = name; _reference = reference; SuperPlan = plan; }
    internal IDataSource? Source { get; }
    internal string Name { get; }
    internal FirmwareSuperImagePlan? SuperPlan { get; }
    internal static FirmwarePackageInput Open(string value, CancellationToken ct, bool requireSuper = false)
    {
        ct.ThrowIfCancellationRequested(); value = ConsolePath.Normalize(value)!;
        int split = value.LastIndexOf("::", StringComparison.Ordinal);
        if (split < 0)
        {
            if (requireSuper) throw new CommandUsageException(FirmwareCommands.Usage);
            return new(new FileDataSource(value), Path.GetFileName(value));
        }
        value = FirmwarePackageReference.Normalize(value); split = value.LastIndexOf("::", StringComparison.Ordinal);
        string entryName = FirmwarePackageReference.Relative(value[(split + 2)..]);
        var reference = FirmwarePackageReference.Open(value[..split], ct);
        try
        {
            string filename = entryName[(entryName.LastIndexOf('/') + 1)..];
            bool definition = requireSuper || filename.Equals("super_def.json", StringComparison.OrdinalIgnoreCase) ||
                filename.StartsWith("super_def.", StringComparison.OrdinalIgnoreCase) && filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (definition)
            {
                var plan = FirmwareSuperImagePlan.Open(reference.Package, entryName, ct);
                return new(null, plan.Name[(plan.Name.LastIndexOf('/') + 1)..], reference, plan);
            }
            var entry = reference.Package.GetEntry(entryName);
            return new(entry, filename, reference);
        }
        catch { reference.Dispose(); throw; }
    }
    public void Dispose() => _reference?.Dispose();
}
