using GeekFlashCore.Protocol.Mtk.Internals;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal sealed partial class LegacySession
{
    private MtkStorageInfo DecodeNand(ulong total,int page,int spare,int pagesPerBlock,byte bmt)
    {
        int erase=checked(page*pagesPerBlock);
        if(page is <512 or >65536 || (page&(page-1))!=0 || spare<0 || spare>page || erase<page || erase>16777216 ||
            bmt>1 || total==0 || total>long.MaxValue || total%(uint)erase!=0)throw new MtkResourceException("Legacy NAND geometry");
        ulong usable=options.NandLogicalCapacity is { } configured?(ulong)configured:total;
        if(usable>total || usable==0 || usable%(uint)erase!=0)throw new MtkResourceException("NAND logical capacity");
        bool confirmed=options.NandLogicalCapacity.HasValue || bmt==0;
        return MtkStorageDecoder.Single(MtkStorageKind.Nand,usable,page,erase,options.EnableNandLogicalWrites&&confirmed) with {
            Nand=new(1,page,spare,erase,total,usable,bmt==1){LogicalCapacityConfirmed=confirmed} };
    }
    public void ReadNand(long offset,long length,Stream output,bool includeSpare)
    {
        var geometry=GetStorage().Nand??throw new MtkCapabilityException("NAND pages");
        // The Legacy DF command uses 32-bit logical byte ranges even on 64-bit geometry devices.
        if(offset<0 || length<=0 || (ulong)offset+(ulong)length>uint.MaxValue || offset%geometry.PageSize!=0 || length%geometry.PageSize!=0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if(!options.LegacyIoT)CheckUsbSpeed();
        wire.Command=0xdf;wire.Write([0xdf,0x0c,0,1]);Write32((uint)offset);Write32((uint)length);Write32(0);Ack();
        uint page=wire.Read32(),spare=wire.Read32(),packet=wire.Read32();
        if(page!=geometry.PageSize || spare!=geometry.SpareSize || packet==0 || packet>options.MaximumFrameSize)throw wire.Failure();
        Write32(1);_ = wire.Read32();
        long rawLength=checked(length/geometry.PageSize*(geometry.PageSize+geometry.SpareSize));int stride=checked(geometry.PageSize+geometry.SpareSize);
        byte[] buffer=ArrayPool<byte>.Shared.Rent((int)packet);
        try
        {
            for(long done=0;done<rawLength;)
            {
                int n=(int)Math.Min(packet,rawLength-done);wire.Read(buffer.AsSpan(0,n));ushort sum=wire.Read16();
                if(sum!=MtkWire.Sum(buffer.AsSpan(0,n)))throw wire.Failure(sum);
                if(includeSpare)output.Write(buffer.AsSpan(0,n));
                else
                {
                    int consumed=0;
                    while(consumed<n)
                    {
                        int inPage=(int)((done+consumed)%stride);int step=Math.Min(n-consumed,stride-inPage);
                        if(inPage<geometry.PageSize)output.Write(buffer.AsSpan(consumed,Math.Min(step,geometry.PageSize-inPage)));
                        consumed+=step;
                    }
                }
                wire.WriteByte(0x5a);done+=n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer,true); }
    }
}
