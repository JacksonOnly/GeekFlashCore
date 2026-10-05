// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard GCPU sequences: B. Kerler, mtkclient hwcrypto_gcpu.py, GPLv3.
using System.Buffers.Binary;
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions.Localization;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Standalone GCPU AES using internal key/data slots and explicit DMA scratch.
/// No memory protection changes, firmware injection or arbitrary DMA destinations.</summary>
public sealed class MtkGcpu
{
    private readonly IMtkHardwareAccess _access;
    private readonly MtkHardwareCryptoProfile _profile;
    public MtkGcpu(IMtkHardwareAccess access,MtkHardwareCryptoProfile profile)
    {
        ArgumentNullException.ThrowIfNull(access);ArgumentNullException.ThrowIfNull(profile);profile.Validate();_access=access;_profile=profile;
    }
    private uint Read(uint offset)=>_access.Read32(checked(_profile.BaseAddress+offset));
    private void Write(uint offset,uint value)=>_access.Write32(checked(_profile.BaseAddress+offset),value);
    private void Slot(uint slot,ReadOnlySpan<byte> data)
    {
        for(int i=0;i<data.Length;i+=4)Write(0xc00+slot*4+(uint)i,BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
    }
    private void Command(uint command,HardwareOperation operation)
    {
        operation.Check();Write(0x804,_profile.HardwareCode==0x8167?1u:3);Write(0x808,_profile.HardwareCode==0x8167?0u:3);
        Write(0xc00,command);Write(0x400,0);
        uint status=operation.Wait(0x800,v=>v!=0);
        if((status&2)!=0)throw new InvalidOperationException(Strings.HardwareFailure);
        operation.Wait(0x418,v=>(v&1)!=0);Write(0x804,_profile.HardwareCode==0x8167?1u:3);
    }
    private void Initialize(HardwareOperation operation)
    {
        operation.Clock(true);uint ctl=Read(0)&0xfffffff0;Write(0,ctl);Write(0,ctl|0xf);
        Write(0,Read(0)&0xffffffe0);Write(4,Read(4)|0x10000);Write(0,Read(0)|0x1f);Write(4,Read(4)|0x2000);
        if(_profile.HardwareCode==0x8167)
        {
            Write(4,(Read(4)&0x7ff0bf7f)|0x34080);Write(0x20,0x885b);Write(0x24,Read(0x24)&0xfffdfffd);Write(0x404,0x80002000);
        }
        for(uint i=1;i<64;i++)Write(0xc00+i*4,0);
    }
    private void Cleanup(HardwareOperation operation,int scratchLength=0)
    {
        for(uint i=1;i<64;i++)Write(0xc00+i*4,0);Write(0x804,3);Write(0,(Read(0)&0xfffffff0)|0xf);
        if(scratchLength>0)ClearScratch(scratchLength);operation.Clock(false);
    }
    private void ClearScratch(int length)
    {
        Span<byte> zero=stackalloc byte[256];zero.Clear();for(int offset=0;offset<length;offset+=zero.Length)_access.WriteMemory(checked(_profile.Scratch.Address+(uint)offset),zero[..Math.Min(zero.Length,length-offset)]);
    }
    /// <summary>Transforms full blocks with the hardware key or a supplied 128-bit internal-slot key.</summary>
    public MtkSensitiveBuffer TransformEcb(ReadOnlySpan<byte> data,bool encrypt,ReadOnlySpan<byte> key=default,CancellationToken cancellationToken=default)
    {
        if(data.IsEmpty || data.Length%16!=0 || data.Length>_profile.MaximumInputSize || key.Length is not (0 or 16))throw new ArgumentException(nameof(data));
        var operation=new HardwareOperation(_access,_profile,cancellationToken);operation.Check();byte[] result=new byte[data.Length];
        try
        {
            Initialize(operation);
            if(key.IsEmpty) { Write(0xc04,0x58);Write(0xc08,0x30);Write(0xc0c,4);Command(0x70,operation); } else Slot(0x30,key);
            for(int pos=0;pos<data.Length;pos+=16)
            {
                operation.Check();Slot(0x12,data.Slice(pos,16));Write(0xc04,1);Write(0xc08,0x30);Write(0xc0c,0x12);Write(0xc10,0x1a);Command(encrypt?0x79u:0x78,operation);
                for(uint i=0;i<4;i++)BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos+(int)i*4),Read(0xc68+i*4));
            }
            Cleanup(operation);operation.Check();return new(result);
        }
        catch { CryptographicOperations.ZeroMemory(result);try { Cleanup(operation); }catch { }try { _access.Invalidate(); }catch { }throw; }
    }
    /// <summary>DMA CBC is confined to two disjoint windows inside the caller-confirmed scratch region.</summary>
    public MtkSensitiveBuffer TransformCbc(ReadOnlySpan<byte> data,bool encrypt,ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> key=default,bool encryptedKey=false,CancellationToken cancellationToken=default)
    {
        if(data.IsEmpty || data.Length%16!=0 || data.Length>_profile.MaximumInputSize || iv.Length!=16 || key.Length is not (0 or 16) ||
            !_profile.Scratch.Contains(_profile.Scratch.Address,checked((uint)data.Length*2)))throw new ArgumentException(nameof(data));
        var operation=new HardwareOperation(_access,_profile,cancellationToken);operation.Check();byte[] result=new byte[data.Length];int scratchLength=checked(data.Length*2);
        try
        {
            Initialize(operation);
            if(key.IsEmpty) { Write(0xc04,0x58);Write(0xc08,0x30);Write(0xc0c,4);Command(0x70,operation); }else Slot(0x30,key);
            Slot(0x1a,iv);uint output=checked(_profile.Scratch.Address+(uint)data.Length);_access.WriteMemory(_profile.Scratch.Address,data);
            Write(0xc04,_profile.Scratch.Address);Write(0xc08,output);Write(0xc0c,(uint)data.Length/16);Write(0xc14,0x30);Write(0xc18,0x1a);Write(0xc1c,0x1a);
            Command(encrypt?0x7du:encryptedKey?0x7eu:0x7cu,operation);_access.ReadMemory(output,result);Cleanup(operation,scratchLength);operation.Check();return new(result);
        }
        catch { CryptographicOperations.ZeroMemory(result);try { Cleanup(operation,scratchLength); }catch { }try { _access.Invalidate(); }catch { }throw; }
    }
    public MtkSensitiveBuffer DeriveMtee(CancellationToken cancellationToken=default)=>TransformEcb("KeymasterMaster\0"u8,true,cancellationToken:cancellationToken);
    /// <summary>Reference device-key HMAC truncated to 16 bytes; the seed includes caller-confirmed devinfo words.</summary>
    public MtkSensitiveBuffer ComputeDeviceHmac(ReadOnlySpan<byte> data,ReadOnlySpan<byte> seed,CancellationToken cancellationToken=default)
    {
        if(seed.Length!=16 || data.Length>_profile.MaximumInputSize)throw new ArgumentException(nameof(seed));
        using var key=TransformEcb(seed,false,cancellationToken:cancellationToken);byte[] digest=HMACSHA256.HashData(key.Memory.Span,data);
        try { return new(digest.AsSpan(0,16).ToArray()); }finally { CryptographicOperations.ZeroMemory(digest); }
    }
    /// <summary>Derives the reference reversed RPMB key from a 16-byte CID and explicitly supplied devinfo seed.</summary>
    public MtkSensitiveBuffer DeriveRpmb(ReadOnlySpan<byte> cid,ReadOnlySpan<byte> seed,CancellationToken cancellationToken=default)
    {
        if(cid.Length!=16 || seed.Length!=16)throw new ArgumentException(nameof(cid));Span<byte> expanded=stackalloc byte[64];for(int i=0;i<64;i++)expanded[i]=cid[i%16];
        try { using var key=ComputeDeviceHmac(expanded,seed,cancellationToken);byte[] result=HMACSHA256.HashData(key.Memory.Span,"RPMB\0"u8);Array.Reverse(result);return new(result); }
        finally { CryptographicOperations.ZeroMemory(expanded); }
    }
}
