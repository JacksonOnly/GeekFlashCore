using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Optional standard XML DA FLASH-UPDATE, using only preflighted images and backups created by this operation.</summary>
public interface IMtkNativeScatterAccess
{
    /// <summary>Sources are borrowed; opened streams are owned. Requires XML scatter and an XML DA.
    /// The DA manages sparse expansion, bootloader headers and protected partition migration.</summary>
    void ApplyXmlScatter(string scatterXml,Func<string,IDataSource> images,IMtkScatterBackupStore backups,
        IProgress<ProgressRecord>? progress=null,CancellationToken cancellationToken=default);
}
