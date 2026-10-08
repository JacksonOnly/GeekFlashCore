using System.Text;
using GeekFlashCore.Android.Sparse;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Da;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol : IMtkNamedPartitionAccess
{
    private static string PartitionName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = MtkPartitionNames.Wire(name);
        if(string.IsNullOrWhiteSpace(name) || name.Length>64 || name.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))throw new ArgumentException(nameof(name));
        return name;
    }
    private void NamedWritePolicy(bool erase=false)
    {
        if(_storage!.Regions.Any(r=>!r.CanWrite))throw new MtkCapabilityException("named partition write policy");
        if(erase && _storage.Kind==MtkStorageKind.Nor && _storage.Regions.Any(r=>r.EraseBlockSize==0))throw new MtkCapabilityException("NOR erase geometry");
    }
    /// <inheritdoc />
    public long ReadNamedPartition(string name,Stream destination,long maximumLength,CancellationToken cancellationToken=default)
    {
        name=PartitionName(name);ArgumentNullException.ThrowIfNull(destination);if(!destination.CanWrite || maximumLength<=0)throw new ArgumentException(nameof(destination));
        return Execute(()=>
        {
            Ready();return _da switch
            {
                XFlashSession x=>x.ReadNamed(name,destination,maximumLength),XmlSession xml=>xml.ReadNamed(name,destination,maximumLength),
                _=>ReadLegacyNamed(name,destination,maximumLength)
            };
        },cancellationToken);
    }
    private long ReadLegacyNamed(string name,Stream destination,long maximum)
    {
        var part=LoadPartitionsCore().SingleOrDefault(p=>MtkPartitionNames.Matches(p.Name,name));
        if(part.Name is null || part.Range.Length>maximum)throw new MtkResourceException("partition name/limit");var region=Range(part.Range);_da!.Read(region,part.Range.Offset,part.Range.Length,destination);return part.Range.Length;
    }
    /// <inheritdoc />
    public void WriteNamedPartition(string name,IDataSource source,long maximumExpandedLength,CancellationToken cancellationToken=default)
    {
        name=PartitionName(name);ArgumentNullException.ThrowIfNull(source);if(maximumExpandedLength<=0)throw new ArgumentOutOfRangeException(nameof(maximumExpandedLength));
        Execute(()=>
        {
            Ready();NamedWritePolicy();if(_da is not (XFlashSession or XmlSession))throw new MtkCapabilityException("native named download/dialect");
            if(source.Length<=0)throw new MtkResourceException("partition source length");
            using Stream input=source.OpenStream();if(!input.CanRead || !input.CanSeek || input.Position!=0 || input.Length!=source.Length)throw new MtkResourceException("partition source");
            if(SparseImageParser.IsSparse(input))
            {
                using var block=new StreamBlockDevice(input,source.Length,DeviceOwnership.Borrow);using var sparse=SparseImageParser.Open(block,DeviceOwnership.Borrow);
                if(sparse.ExpandedLength<=0 || sparse.ExpandedLength>maximumExpandedLength)throw new MtkResourceException("partition sparse capacity");sparse.VerifyChecksum(cancellationToken:cancellationToken);input.Position=0;
            }
            else if(source.Length>maximumExpandedLength)throw new MtkResourceException("partition source capacity");
            if(_da is XFlashSession x)x.WriteNamed(name,input,source.Length);else ((XmlSession)_da).WriteNamed(name,input,source.Length);
            _partitions=null;return 0;
        },cancellationToken);
    }
    /// <inheritdoc />
    public void EraseNamedPartition(string name,CancellationToken cancellationToken=default)
    {
        name=PartitionName(name);Execute(()=>
        {
            Ready();NamedWritePolicy(erase:true);if(_da is XFlashSession x)x.EraseNamed(name);else if(_da is XmlSession xml)xml.EraseNamed(name);
            else
            {
                var part=LoadPartitionsCore().SingleOrDefault(p=>MtkPartitionNames.Matches(p.Name,name));if(part.Name is null)throw new MtkResourceException("partition name");
                var region=Range(part.Range);_da!.Erase(region,part.Range.Offset,part.Range.Length);
            }
            _partitions=null;return 0;
        },cancellationToken);
    }
}
