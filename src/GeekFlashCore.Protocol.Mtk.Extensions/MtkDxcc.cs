// SPDX-License-Identifier: AGPL-3.0-or-later
// DXCC descriptors: B. Kerler, mtkclient hwcrypto_dxcc.py, GPLv3.
using System.Buffers.Binary;
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions.Localization;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Exportable standard DXCC key selectors. Provisioning/engine-lock operations are separate and excluded.</summary>
public enum MtkDxccKey : uint { User=0,Root=1,Session=3,Platform=5,Customer=6,Fde0=7,Fde1=9,Fde2=10,Fde3=11 }
/// <summary>Bounded six-word DXCC/TZCC queue, CMAC/KDF/SHA256 and read-only OTP access.</summary>
public sealed class MtkDxcc
{
    private readonly IMtkHardwareAccess _access;
    private readonly MtkHardwareCryptoProfile _profile;
    public MtkDxcc(IMtkHardwareAccess access,MtkHardwareCryptoProfile profile)
    { ArgumentNullException.ThrowIfNull(access);ArgumentNullException.ThrowIfNull(profile);profile.Validate();_access=access;_profile=profile; }
    private void Write(uint offset,uint value)=>_access.Write32(checked(_profile.BaseAddress+offset),value);
    private uint Read(uint offset)=>_access.Read32(checked(_profile.BaseAddress+offset));
    private void Queue(ReadOnlySpan<uint> descriptor,HardwareOperation operation)
    {
        operation.Wait(0xe9c,v=>(v&0xf)!=0);for(uint i=0;i<6;i++)Write(0xe80+i*4,descriptor[(int)i]);
    }
    private void Complete(HardwareOperation operation)
    {
        Write(0xa08,4);Queue([0,0x8000011,checked(_profile.Scratch.Address+0x3c),0x8000012,0x100,0],operation);
        operation.Wait(0xa00,v=>(v&4)!=0);uint status=operation.Wait(0xba0,v=>v!=0);
        if(status!=1)throw new InvalidOperationException(Strings.HardwareFailure);Write(0xa08,4);
    }
    private int Layout(int inputLength,int keyLength=0)
    {
        int length=checked(0x40+((inputLength+3)&~3)+keyLength);
        if(inputLength>_profile.MaximumInputSize || !_profile.Scratch.Contains(_profile.Scratch.Address,(uint)length))throw new ArgumentOutOfRangeException(nameof(inputLength));return length;
    }
    private void Store(uint address,ReadOnlySpan<byte> data)
    {
        int whole=data.Length&~3;if(whole>0)_access.WriteMemory(address,data[..whole]);
        if(whole<data.Length) { Span<byte> tail=stackalloc byte[4];tail.Clear();data[whole..].CopyTo(tail);_access.WriteMemory(checked(address+(uint)whole),tail); }
    }
    private void Cleanup(int length,HardwareOperation operation)
    {
        Span<byte> zeros=stackalloc byte[256];zeros.Clear();for(int i=0;i<length;i+=zeros.Length)_access.WriteMemory(checked(_profile.Scratch.Address+(uint)i),zeros[..Math.Min(zeros.Length,length-i)]);
        operation.Clock(false);
    }
    /// <summary>Computes CMAC using a supplied 128-bit key or a selected hardware key; never returns before queue completion.</summary>
    public MtkSensitiveBuffer ComputeCmac(ReadOnlySpan<byte> data,MtkDxccKey key,ReadOnlySpan<byte> userKey=default,CancellationToken cancellationToken=default)
    {
        if(!Enum.IsDefined(key) || key==MtkDxccKey.User && userKey.Length!=16 || key!=MtkDxccKey.User && !userKey.IsEmpty)throw new ArgumentException(nameof(key));
        int length=Layout(data.Length,userKey.Length);var operation=new HardwareOperation(_access,_profile,cancellationToken);operation.Check();byte[] result=new byte[16];
        try
        {
            operation.Clock(true);uint input=checked(_profile.Scratch.Address+0x40),keyAddress=checked(input+(uint)((data.Length+3)&~3));
            Store(input,data);if(!userKey.IsEmpty)Store(keyAddress,userKey);Write(0xa08,4);
            uint keySize=key==MtkDxccKey.Root && (Read(0xaa0)&2)!=0?0x800000u:0;
            Queue([0,0x8000041,0,0,keySize|0x1001c20,0],operation);
            Queue([key==MtkDxccKey.User?keyAddress:0,key==MtkDxccKey.User?0x41u:0,0,0,keySize|0x4001c20|(((uint)key&3)<<15)|((((uint)key>>2)&3)<<20),0],operation);
            // DMA_DLLI reads exactly the meaningful byte count, including an empty CMAC message.
            Queue([input,checked((uint)data.Length*4)|2,0,0,1,0],operation);
            Queue([0,0,_profile.Scratch.Address,0x42,0x8001c26,0],operation);
            Complete(operation);_access.ReadMemory(_profile.Scratch.Address,result);Cleanup(length,operation);operation.Check();return new(result);
        }
        catch { CryptographicOperations.ZeroMemory(result);try { Cleanup(length,operation); }catch { }try { _access.Invalidate(); }catch { }throw; }
    }
    /// <summary>Reference one-byte-counter CMAC KDF. Output length must be a multiple of 16, at most 240 bytes.</summary>
    public MtkSensitiveBuffer DeriveKey(MtkDxccKey key,ReadOnlySpan<byte> label,ReadOnlySpan<byte> salt,int length=32,CancellationToken cancellationToken=default)
    {
        if(key is not (MtkDxccKey.Root or MtkDxccKey.Platform) || label.Length is <1 or >32 || salt.Length>1024 || length is <16 or >240 || length%16!=0)throw new ArgumentException(nameof(length));
        int inputLength=checked(label.Length+salt.Length+3);_ = Layout(inputLength);byte[] input=new byte[inputLength],result=new byte[length];
        try
        {
            label.CopyTo(input.AsSpan(1));salt.CopyTo(input.AsSpan(label.Length+2));input[^1]=unchecked((byte)(length*8));
            for(int i=0;i<length/16;i++) { input[0]=(byte)(i+1);using var part=ComputeCmac(input,key,cancellationToken:cancellationToken);part.Memory.Span.CopyTo(result.AsSpan(i*16)); }
            return new(result);
        }
        catch { CryptographicOperations.ZeroMemory(result);throw; }finally { CryptographicOperations.ZeroMemory(input); }
    }
    public MtkSensitiveBuffer DeriveRpmb(int level=0,CancellationToken cancellationToken=default)
    {
        if(level is <0 or >3)throw new ArgumentOutOfRangeException(nameof(level));Span<byte> label=stackalloc byte[8];"RPMB KEY"u8.CopyTo(label);Span<byte> salt=stackalloc byte[4];"SASI"u8.CopyTo(salt);
        for(int i=0;i<label.Length;i++)label[i]+=(byte)level;for(int i=0;i<salt.Length;i++)salt[i]+=(byte)level;
        try { return DeriveKey(MtkDxccKey.Root,label,salt,level==0?32:16,cancellationToken); }finally { CryptographicOperations.ZeroMemory(label);CryptographicOperations.ZeroMemory(salt); }
    }
    public MtkSensitiveBuffer DeriveMiteeRpmb(CancellationToken cancellationToken=default)=>DeriveKey(MtkDxccKey.Root,Convert.FromHexString("AD1AC6B4BDF4EDB7"),Convert.FromHexString("69EF6584"),16,cancellationToken);
    public MtkSensitiveBuffer DeriveItrustee(ReadOnlySpan<byte> applicationId,int length=32,CancellationToken cancellationToken=default)
    {
        if(applicationId.Length>1024 || length is <16 or >240 || length%16!=0)throw new ArgumentException(nameof(length));
        byte[] input=new byte[37+applicationId.Length],result=new byte[length];"TrustedCorekeymaster"u8.CopyTo(input);input.AsSpan(20,16).Fill(7);applicationId.CopyTo(input.AsSpan(36));
        _ = Layout(input.Length);
        try { for(int i=0;i<length/16;i++) { input[^1]=(byte)i;using var part=ComputeCmac(input,MtkDxccKey.Root,cancellationToken:cancellationToken);part.Memory.Span.CopyTo(result.AsSpan(i*16)); }return new(result); }
        catch { CryptographicOperations.ZeroMemory(result);throw; }finally { CryptographicOperations.ZeroMemory(input); }
    }
    /// <summary>Hashes bounded input through the SHA256 hardware descriptors, not the host SHA implementation.</summary>
    public MtkSensitiveBuffer ComputeSha256(ReadOnlySpan<byte> data,CancellationToken cancellationToken=default)
    {
        int length=Layout(data.Length);var operation=new HardwareOperation(_access,_profile,cancellationToken);operation.Check();byte[] result=new byte[32];
        try
        {
            operation.Clock(true);uint iv=checked(_profile.Scratch.Address+0x20),input=checked(_profile.Scratch.Address+0x40);
            Store(iv,Convert.FromHexString("19CDE05BABD9831F8C68059B7F520E513AF54FA572F36E3C85AE67BB67E6096A"));Store(input,data);
            Queue([iv,0x82,0,0,0x1000825,0],operation);Queue([0,0x8000041,0,0,0x4000825,0],operation);
            Queue([0,0,_profile.Scratch.Address,0x42,0x908082b,0],operation);Queue([input,checked((uint)data.Length*4)|2,0,0,7,0],operation);
            Queue([0,0,_profile.Scratch.Address,0x82,0x80c082b,0],operation);Complete(operation);
            _access.ReadMemory(_profile.Scratch.Address,result);Cleanup(length,operation);operation.Check();return new(result);
        }
        catch { CryptographicOperations.ZeroMemory(result);try { Cleanup(length,operation); }catch { }try { _access.Invalidate(); }catch { }throw; }
    }
    /// <summary>Reads one OTP word (0..0x24); never programs an OTP cell.</summary>
    public uint ReadOtpWord(uint index,CancellationToken cancellationToken=default)
    {
        if(index>0x24)throw new ArgumentOutOfRangeException(nameof(index));var operation=new HardwareOperation(_access,_profile,cancellationToken);operation.Check();
        try { operation.Clock(true);operation.Wait(0xabc,v=>(v&1)!=0);Write(0xaa4,index*4|0x10000);operation.Wait(0xab4,v=>(v&1)!=0);uint result=Read(0xaac);operation.Clock(false);operation.Check();return result; }
        catch { try { operation.Clock(false); }catch { }try { _access.Invalidate(); }catch { }throw; }
    }
    public MtkSensitiveBuffer ReadPublicKeyHash(bool secondary=false,CancellationToken cancellationToken=default)
    {
        byte[] result=new byte[secondary?16:32];try { for(uint i=0;i<result.Length/4;i++)BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan((int)i*4),ReadOtpWord((secondary?0x14u:0x10)+i,cancellationToken));return new(result); }
        catch { CryptographicOperations.ZeroMemory(result);throw; }
    }
    public MtkSensitiveBuffer ComputeSocId(CancellationToken cancellationToken=default)
    {
        using var publicHash=ReadPublicKeyHash(cancellationToken:cancellationToken);using var derived=DeriveKey(MtkDxccKey.Root,[0x49],new byte[32],16,cancellationToken);
        Span<byte> input=stackalloc byte[48];publicHash.Memory.Span.CopyTo(input);derived.Memory.Span.CopyTo(input[32..]);try { return new(SHA256.HashData(input)); }finally { CryptographicOperations.ZeroMemory(input); }
    }
}
