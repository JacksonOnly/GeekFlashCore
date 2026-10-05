namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Optional NAND page access. Ranges address logical data bytes; output additionally contains each page's spare bytes.</summary>
public interface IMtkNandAccess
{
    /// <summary>Streams data+spare pages with checksum verification. Does not change bad-block mappings or perform physical writes.</summary>
    void ReadNandPages(MtkFlashRange logicalRange, Stream destination, CancellationToken cancellationToken = default);
}
