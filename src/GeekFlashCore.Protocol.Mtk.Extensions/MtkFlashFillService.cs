using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Explicit logical byte-pattern overwrite using ordinary DA writes. This does not physically erase NAND/NOR or write OOB.</summary>
public sealed class MtkFlashFillService
{
    private readonly IMtkSessionAccess _access;
    public MtkFlashFillService(IMtkProtocol protocol)
    {ArgumentNullException.ThrowIfNull(protocol);_access=protocol as IMtkSessionAccess??throw new MtkCapabilityException("scoped DA channel");}
    /// <summary>Overwrites the requested aligned writable range; default verification streams a full readback.
    /// Allocations are bounded independently of range length.</summary>
    public void Fill(MtkFlashRange range,byte value=0,bool verify=true,IProgress<ProgressRecord>? progress=null,CancellationToken cancellationToken=default)
    {
        _access.UseSession(c=>
        {
            var region=c.Storage.Regions.SingleOrDefault(r=>r.WireId==range.RegionId)??throw new MtkCapabilityException("fill region");
            if(!region.CanWrite)throw new MtkCapabilityException("fill writable storage");
            if(range.Offset<0 || range.Length<=0 || range.Offset>region.Length-range.Length || range.Offset%region.BlockSize!=0 || range.Length%region.BlockSize!=0)throw new ArgumentOutOfRangeException(nameof(range));
            bool writing=false;
            try
            {
                progress?.Report(new(0,range.Length,"fill"){Phase=ProgressPhase.Started});
                using var source=new PatternStream(range.Length,value,progress,cancellationToken);writing=true;c.WriteFlash(range,source);
                if(verify){using var result=new PatternStream(range.Length,value,null,cancellationToken);c.ReadFlash(range,result);if(result.Count!=range.Length)throw new MtkResourceException("fill readback length");}
                _ = c.Generation;progress?.Report(new(range.Length,range.Length,"fill"){Phase=ProgressPhase.Completed});return 0;
            }
            catch(Exception ex)when(writing){try{c.Invalidate();}catch{}throw new MtkFlashFillException(ex);}
        },cancellationToken);
    }
    private sealed class PatternStream(long length,byte value,IProgress<ProgressRecord>? progress,CancellationToken token):Stream
    {
        public long Count{get;private set;}
        public override int Read(Span<byte> data)
        {token.ThrowIfCancellationRequested();int n=(int)Math.Min(data.Length,length-Count);data[..n].Fill(value);Count+=n;progress?.Report(new(Count,length,"fill"){Phase=ProgressPhase.Running});return n;}
        public override void Write(ReadOnlySpan<byte> data)
        {token.ThrowIfCancellationRequested();if(data.Length>length-Count || data.IndexOfAnyExcept(value)>=0)throw new MtkResourceException("fill readback");Count+=data.Length;}
        public override int Read(byte[] b,int o,int n)=>Read(b.AsSpan(o,n));public override void Write(byte[] b,int o,int n)=>Write(b.AsSpan(o,n));
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;public override long Length=>length;public override long Position{get=>Count;set=>throw new NotSupportedException();}
        public override void Flush(){}public override long Seek(long o,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }
}
