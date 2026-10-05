using System.Buffers.Binary;
using GeekFlashCore.Protocol.Mtk.Da;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol : IMtkDaHardwareSessionAccess
{
    /// <inheritdoc />
    public T UseDaHardware<T>(IReadOnlyList<MtkMemoryRange> allowedRanges,Func<IMtkHardwareAccess,T> action,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(allowedRanges);ArgumentNullException.ThrowIfNull(action);var ranges=allowedRanges.ToArray();
        if(ranges.Length is <1 or >256 || ranges.Any(r=>!r.Contains(r.Address,r.Length) || (r.Address&3)!=0 || (r.Length&3)!=0))throw new ArgumentException(nameof(allowedRanges));
        return Execute(()=>
        {
            Ready();if(_da is not (LegacySession or XmlSession))throw new MtkCapabilityException("standard DA hardware access/dialect");
            var access=new DaHardwareChannel(this,ranges);try { return action(access); }finally { access.Expire(); }
        },cancellationToken);
    }
    private sealed class DaHardwareChannel(MtkProtocol owner,MtkMemoryRange[] ranges) : IMtkHardwareAccess
    {
        private bool _valid=true;private readonly long _generation=owner.Generation;private readonly int _thread=Environment.CurrentManagedThreadId;
        public void Expire()=>_valid=false;
        public void Check()
        {
            if(!_valid || _generation!=owner.Generation || _thread!=Environment.CurrentManagedThreadId)throw new InvalidOperationException(Strings.SessionUnavailable);
            owner.Ready();owner._wire.Check();
        }
        public void Invalidate()
        {
            if(!_valid || _generation!=owner.Generation || _thread!=Environment.CurrentManagedThreadId)throw new InvalidOperationException(Strings.SessionUnavailable);
            owner.Fault();_valid=false;
        }
        private void Range(uint address,int length)
        {
            Check();if(length<=0 || length>1048576 || (address&3)!=0 || (length&3)!=0 || !ranges.Any(r=>r.Contains(address,(uint)length)))throw new MtkCapabilityException("hardware access range");
        }
        public uint Read32(uint address)
        { Range(address,4);return owner._da is LegacySession legacy?legacy.ReadRegister(address):((XmlSession)owner._da!).ReadRegister(address); }
        public void Write32(uint address,uint value)
        { Range(address,4);if(owner._da is LegacySession legacy)legacy.WriteRegister(address,value);else ((XmlSession)owner._da!).WriteRegister(address,value); }
        public void ReadMemory(uint address,Span<byte> destination)
        { Range(address,destination.Length);for(int i=0;i<destination.Length;i+=4)BinaryPrimitives.WriteUInt32LittleEndian(destination[i..],Read32(checked(address+(uint)i))); }
        public void WriteMemory(uint address,ReadOnlySpan<byte> data)
        { Range(address,data.Length);for(int i=0;i<data.Length;i+=4)Write32(checked(address+(uint)i),BinaryPrimitives.ReadUInt32LittleEndian(data[i..])); }
    }
}
