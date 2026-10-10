using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Standard DA-native named partition operations, including DA-managed sparse/BROM header handling.</summary>
/// <remarks>Canonical auxiliary names are preloader, preloader_backup, pgpt and sgpt.
/// Historical display aliases remain accepted without case sensitivity. Preloader uses native named
/// upload/download with DA-managed headers; unsupported dialects do not fall back to raw boot-region I/O.</remarks>
public interface IMtkNamedPartitionAccess
{
    long ReadNamedPartition(string name,Stream destination,long maximumLength,CancellationToken cancellationToken=default);
    /// <summary>The caller supplies a finite expanded-size limit. The DA additionally enforces actual partition capacity.</summary>
    void WriteNamedPartition(string name,IDataSource source,long maximumExpandedLength,CancellationToken cancellationToken=default);
    void EraseNamedPartition(string name,CancellationToken cancellationToken=default);
}
