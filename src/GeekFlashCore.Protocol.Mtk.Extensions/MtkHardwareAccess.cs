using System.Buffers.Binary;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions.Localization;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Executes hardware drivers through standard BROM access, without acquiring or bypassing permissions.</summary>
public static class MtkHardwareSession
{
    public static T UseBrom<T>(IMtkBromSessionAccess protocol, Func<IMtkHardwareAccess,T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protocol); ArgumentNullException.ThrowIfNull(action);
        return protocol.UseBromSession(session =>
        {
            var access = new BromAccess(session, cancellationToken);
            try { return action(access); }
            finally { access.Expire(); }
        }, cancellationToken);
    }
    private sealed class BromAccess(IMtkBromSession session, CancellationToken token) : IMtkHardwareAccess
    {
        private bool _valid = true;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        public void Expire() => _valid = false;
        public void Check()
        {
            if (!_valid || _thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException(Strings.ExtensionUnavailable);
            token.ThrowIfCancellationRequested();
            if (session is IMtkBromSessionControl control) control.Check();
        }
        public void Invalidate()
        {
            if(!_valid || _thread!=Environment.CurrentManagedThreadId)throw new InvalidOperationException(Strings.ExtensionUnavailable);
            if (session is IMtkBromSessionControl control) control.Invalidate(); _valid = false;
        }
        public uint Read32(uint address) { Check(); return session.Read32(address)[0]; }
        public void Write32(uint address, uint value) { Check(); session.Write32(address, [value]); }
        public void ReadMemory(uint address, Span<byte> destination)
        {
            Check(); Validate(address,destination.Length);
            for (int i=0;i<destination.Length;i+=4) BinaryPrimitives.WriteUInt32LittleEndian(destination[i..],Read32(checked(address+(uint)i)));
        }
        public void WriteMemory(uint address, ReadOnlySpan<byte> data) { Check(); Validate(address,data.Length); session.WriteMemory(address,data); }
        private static void Validate(uint address,int length)
        {
            if ((address & 3)!=0 || length<=0 || (length & 3)!=0 || (ulong)address+(uint)length>(ulong)uint.MaxValue+1)
                throw new ArgumentOutOfRangeException(nameof(address));
        }
    }
}

internal sealed class HardwareOperation(IMtkHardwareAccess access, MtkHardwareCryptoProfile profile, CancellationToken token)
{
    private readonly long _deadline = checked(Environment.TickCount64 + profile.TimeoutMilliseconds);
    public void Check()
    {
        token.ThrowIfCancellationRequested(); access.Check();
        if (Environment.TickCount64 >= _deadline) throw new TimeoutException(Strings.HardwareTimeout);
    }
    public uint Wait(uint offset, Func<uint,bool> ready)
    {
        for(int i=0;i<profile.MaximumPolls;i++) { Check(); uint value=access.Read32(checked(profile.BaseAddress+offset)); if(ready(value))return value; }
        throw new TimeoutException(Strings.HardwareTimeout);
    }
    public void Clock(bool enabled)
    {
        uint address=enabled?profile.ClockEnableAddress:profile.ClockDisableAddress;
        if(address!=0) access.Write32(address,enabled?profile.ClockEnableValue:profile.ClockDisableValue);
    }
}
