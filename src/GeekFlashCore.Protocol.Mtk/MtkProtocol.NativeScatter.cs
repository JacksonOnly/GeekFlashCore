using System.Text;
using GeekFlashCore.Android.Sparse;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Da;
using GeekFlashCore.Protocol.Mtk.Loaders;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol : IMtkNativeScatterAccess
{
    /// <inheritdoc />
    public void ApplyXmlScatter(string scatterXml,Func<string,IDataSource> images,IMtkScatterBackupStore backups,
        IProgress<ProgressRecord>? progress=null,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(scatterXml);ArgumentNullException.ThrowIfNull(images);ArgumentNullException.ThrowIfNull(backups);
        if(!scatterXml.AsSpan().TrimStart().StartsWith("<"))throw new MtkResourceException("XML scatter required");
        var manifest=MtkScatterManifestParser.Parse(scatterXml);
        if(manifest.Partitions.Any(p=>p.Host is >0))throw new MtkCapabilityException("multi-host XML scatter requires separate geometry");
        Execute(()=>
        {
            Ready();if(_da is not XmlSession xml)throw new MtkCapabilityException("XML FLASH-UPDATE");
            var plan=MtkScatterPlanBuilder.Create(manifest,_storage!,_generation);
            if(plan.Partitions.Any(p=>p.Download && !_storage!.Regions.Single(r=>r.WireId==p.Range.RegionId).CanWrite))throw new MtkCapabilityException("scatter writable storage");
            var files=new Dictionary<string,(Stream Source,long Length)>(StringComparer.Ordinal);
            try
            {
                foreach(var group in plan.Partitions.Where(p=>p.Download && p.FileName!=null).GroupBy(p=>p.FileName!,StringComparer.Ordinal))
                {
                    string name=group.Key;if(name=="scatter.xml")throw new MtkResourceException("scatter image name");
                    var data=images(name)??throw new MtkResourceException("scatter image");long size=data.Length;
                    if(size<=0)throw new MtkResourceException("scatter image length");
                    Stream input=data.OpenStream();try
                    {
                        if(!input.CanRead || !input.CanSeek || input.Position!=0 || input.Length!=size)throw new MtkResourceException("scatter image stream");
                        long limit=group.Min(p=>p.Range.Length);
                        if(SparseImageParser.IsSparse(input))
                        {
                            using var block=new StreamBlockDevice(input,size,DeviceOwnership.Borrow);using var sparse=SparseImageParser.Open(block,DeviceOwnership.Borrow);
                            if(sparse.ExpandedLength<=0 || sparse.ExpandedLength>limit)throw new MtkResourceException("scatter sparse capacity");
                            sparse.VerifyChecksum(cancellationToken:cancellationToken);
                        }
                        else if(size>limit)throw new MtkResourceException("scatter image capacity");
                        input.Position=0;files.Add(name,(input,size));input=null!;
                    }
                    finally {input?.Dispose();}
                }
                if(files.Count==0)throw new MtkResourceException("scatter no downloadable image");
                long maximumBackup=_storage!.Regions.Max(r=>r.Length);
                byte[] text=new UTF8Encoding(false,true).GetBytes(scatterXml);using var source=new MemoryStream(text,false);
                try {xml.FlashUpdate(source,text.Length,files,backups,maximumBackup,progress);_partitions=null;_wire.Check();}
                catch(Exception ex)when(_wire.HasWritten){throw new MtkScatterWriteException(ex);}
                finally {System.Security.Cryptography.CryptographicOperations.ZeroMemory(text);}
                return 0;
            }
            finally {foreach(var file in files.Values)file.Source.Dispose();}
        },cancellationToken);
    }
}
