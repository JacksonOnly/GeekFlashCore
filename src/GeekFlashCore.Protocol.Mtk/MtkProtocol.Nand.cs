using GeekFlashCore.Protocol.Mtk.Da;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol : IMtkNandAccess
{
    /// <inheritdoc />
    public void ReadNandPages(MtkFlashRange logicalRange,Stream destination,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Execute(()=>
        {
            var region=Range(logicalRange);if(!destination.CanWrite)throw new ArgumentException(nameof(destination));
            if(region.Kind!=MtkStorageKind.Nand || _da is not LegacySession legacy)throw new MtkCapabilityException("Legacy NAND spare pages");
            legacy.ReadNand(logicalRange.Offset,logicalRange.Length,destination,true);return 0;
        },cancellationToken);
    }
}
