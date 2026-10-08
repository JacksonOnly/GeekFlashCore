using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Standard DA-native named partition operations, including DA-managed sparse/BROM header handling.</summary>
/// <remarks>PGPT/PrimaryGPT, SGPT/BackupGPT, Preloader and preloader_backup/Preloader Backup
/// are matched without case sensitivity and translated to the DA's partition names.</remarks>
public interface IMtkNamedPartitionAccess
{
    long ReadNamedPartition(string name,Stream destination,long maximumLength,CancellationToken cancellationToken=default);
    /// <summary>The caller supplies a finite expanded-size limit. The DA additionally enforces actual partition capacity.</summary>
    void WriteNamedPartition(string name,IDataSource source,long maximumExpandedLength,CancellationToken cancellationToken=default);
    void EraseNamedPartition(string name,CancellationToken cancellationToken=default);
}
