// SPDX-License-Identifier: AGPL-3.0-or-later
// SEJ register sequences: B. Kerler, mtkclient hwcrypto_sej.py, GPLv3.
using System.Buffers.Binary;
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Standalone SEJ driver using standard authorized register access. No DAPC or security bypass.</summary>
public sealed class MtkSej
{
    private readonly IMtkHardwareAccess _access;
    private readonly MtkHardwareCryptoProfile _profile;
    private static ReadOnlySpan<uint> Pattern => [0x2D44BB70,0xA744D227,0xD0A9864B,0x83FFC244,0x7EC8266B,0x43E80FB2,0x01A6348A,0x2067F9A0,0x54536405,0xD546A6B1,0x1CC3EC3A,0xDE377A83];
    private static ReadOnlySpan<uint> DefaultIv => [0x9ED40400,0x00E884A1,0xE3F083BD,0x2F4E6D8A];
    public MtkSej(IMtkHardwareAccess access,MtkHardwareCryptoProfile profile)
    {
        ArgumentNullException.ThrowIfNull(access); ArgumentNullException.ThrowIfNull(profile); profile.Validate(); _access=access; _profile=profile;
    }
    private uint Read(uint offset)=>_access.Read32(checked(_profile.BaseAddress+offset));
    private void Write(uint offset,uint value)=>_access.Write32(checked(_profile.BaseAddress+offset),value);
    private void Words(uint offset,ReadOnlySpan<byte> bytes,bool bigEndian=false)
    {
        for(int i=0;i<bytes.Length;i+=4) Write(offset+(uint)i,bigEndian?BinaryPrimitives.ReadUInt32BigEndian(bytes[i..]):BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]));
    }
    private void ClearKeys() { for(uint offset=0x20;offset<0x40;offset+=4)Write(offset,0); }
    private void RunBlock(ReadOnlySpan<byte> input,Span<byte> output,HardwareOperation operation)
    {
        operation.Check(); Words(0x10,input); Write(8,1); operation.Wait(8,v=>(v&0x8000)!=0);
        for(uint i=0;i<4;i++)BinaryPrimitives.WriteUInt32LittleEndian(output[(int)(i*4)..],Read(0x50+i*4));
    }
    /// <summary>Encrypts/decrypts complete AES blocks with a supplied software key, or raw hardware key when key is empty.</summary>
    public MtkSensitiveBuffer Transform(ReadOnlySpan<byte> data,bool encrypt,MtkAesMode mode,
        ReadOnlySpan<byte> key=default,ReadOnlySpan<byte> iv=default,CancellationToken cancellationToken=default) =>
        TransformCore(data,encrypt,mode,key,iv,false,false,default,cancellationToken);
    /// <summary>Uses the reference V3 key-feedback initialization; legacy selects the older hardware handshake.</summary>
    public MtkSensitiveBuffer TransformV3(ReadOnlySpan<byte> data,bool encrypt,ReadOnlySpan<byte> iv=default,
        bool legacy=false,ReadOnlySpan<byte> otp=default,CancellationToken cancellationToken=default)
    {
        Span<byte> defaultIv=stackalloc byte[16];
        for(int i=0;i<4;i++)BinaryPrimitives.WriteUInt32LittleEndian(defaultIv[(i*4)..],DefaultIv[i]);
        return TransformCore(data,encrypt,MtkAesMode.Cbc,default,iv.IsEmpty?defaultIv:iv,true,legacy,otp,cancellationToken);
    }
    private MtkSensitiveBuffer TransformCore(ReadOnlySpan<byte> data,bool encrypt,MtkAesMode mode,
        ReadOnlySpan<byte> key,ReadOnlySpan<byte> iv,bool v3,bool legacy,ReadOnlySpan<byte> otp,CancellationToken token)
    {
        if(data.IsEmpty || data.Length%16!=0 || data.Length>_profile.MaximumInputSize || !Enum.IsDefined(mode) ||
            key.Length is not (0 or 16 or 24 or 32) || mode==MtkAesMode.Cbc && iv.Length!=16 || mode==MtkAesMode.Ecb && !iv.IsEmpty || otp.Length is not (0 or 32))
            throw new ArgumentException(nameof(data));
        var operation=new HardwareOperation(_access,_profile,token); operation.Check();
        byte[] result=new byte[data.Length]; bool touched=false;
        try
        {
            touched=true; operation.Clock(true); if(!otp.IsEmpty)Words(0x60,otp);
            ClearKeys(); uint config=(encrypt?1u:0)|(mode==MtkAesMode.Cbc?2u:0)|(key.Length==24?0x10u:key.Length==32?0x20u:0);
            Write(0x80,1);
            if(v3)
            {
                Write(4,2); Write(0xc,0x110); Write(8,2); Words(0x40,iv);
                if(legacy)
                {
                    Write(0xbc,Read(0xbc)|2);Write(8,Read(8)|0x40000000);operation.Wait(8,v=>v>0x80000000);
                    Write(0xbc,Read(0xbc)&0xfffffffe);Write(0xc,0x10);
                }
                else
                {
                    Write(0xbc,1);
                    for(int i=0;i<3;i++) { for(uint j=0;j<4;j++)Write(0x10+j*4,Pattern[i*4+(int)j]); Write(8,1);operation.Wait(8,v=>(v&0x8000)!=0); }
                    Write(8,2);Words(0x40,iv);Write(0xc,0);
                }
                Write(4,config);
            }
            else
            {
                Write(4,config); Write(0xc,key.IsEmpty?0x10u:0);if(!key.IsEmpty)Words(0x20,key,true);Write(8,2);if(!iv.IsEmpty)Words(0x40,iv);
            }
            for(int i=0;i<data.Length;i+=16)RunBlock(data.Slice(i,16),result.AsSpan(i,16),operation);
            operation.Check();
            // Cleanup is part of success: a failed cleanup must not return a usable result.
            Cleanup(!otp.IsEmpty,operation); touched=false; return new(result);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            if(touched) { try { Cleanup(!otp.IsEmpty,operation); } catch { } try { _access.Invalidate(); } catch { } }
            throw;
        }
    }
    private void Cleanup(bool otp,HardwareOperation operation)
    {
        Write(8,2);ClearKeys();Write(0xc,0);for(uint offset=0x40;offset<0x50;offset+=4)Write(offset,0);
        if(otp)for(uint offset=0x60;offset<0x80;offset+=4)Write(offset,0);operation.Clock(false);
    }
    /// <summary>Derives 16/32-byte RPMB material with the caller's MEID and temporary OTP.</summary>
    public MtkSensitiveBuffer DeriveRpmb(ReadOnlySpan<byte> meid,ReadOnlySpan<byte> otp,int length=32,CancellationToken cancellationToken=default)
    {
        if(meid.IsEmpty || meid.Length>32 || otp.Length!=32 || length is not (16 or 32))throw new ArgumentException(nameof(meid));
        Span<byte> input=stackalloc byte[length];for(int i=0;i<length;i++)input[i]=meid[i%meid.Length];
        try { return TransformV3(input,true,otp:otp,cancellationToken:cancellationToken); } finally { CryptographicOperations.ZeroMemory(input); }
    }
    /// <summary>Derives reference MTEE keymaster material.</summary>
    public MtkSensitiveBuffer DeriveMtee(bool hardwareLabel=false,ReadOnlySpan<byte> otp=default,CancellationToken cancellationToken=default) =>
        TransformV3(hardwareLabel?"www.mediatek.com0123456789ABCDEF"u8:"KeymasterMaster\0"u8,true,otp:otp,cancellationToken:cancellationToken);
    /// <summary>Normal META cipher with the reference custom seed, or an explicitly supplied seed.</summary>
    public MtkSensitiveBuffer TransformMeta(ReadOnlySpan<byte> data,bool encrypt,uint seed=0xbb13be00,
        bool legacy=false,ReadOnlySpan<byte> otp=default,CancellationToken cancellationToken=default)
    {
        uint rotated=(seed>>16)|(seed<<16);Span<byte> iv=stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(iv,seed);BinaryPrimitives.WriteUInt32LittleEndian(iv[4..],~seed);
        BinaryPrimitives.WriteUInt32LittleEndian(iv[8..],rotated);BinaryPrimitives.WriteUInt32LittleEndian(iv[12..],~rotated);
        try{return TransformV3(data,encrypt,iv,legacy,otp,cancellationToken);}finally{CryptographicOperations.ZeroMemory(iv);}
    }
    /// <summary>Wraps repeated MEID material with the hardware key, then performs the reference two AES-256 passes.
    /// All intermediate key material is cleared; no persistent OTP is programmed.</summary>
    public MtkSensitiveBuffer DeriveMteeFromMeid(ReadOnlySpan<byte> meid,ReadOnlySpan<byte> otp=default,CancellationToken cancellationToken=default)
    {
        if(meid.Length is not (16 or 32) || otp.Length is not (0 or 32))throw new ArgumentException(nameof(meid));
        Span<byte> input=stackalloc byte[32];for(int i=0;i<input.Length;i++)input[i]=meid[i%meid.Length];
        byte[] iv=Convert.FromHexString("57325A5A125497661254976657325A5A");
        try
        {
            using var wrapped=TransformCore(input,true,MtkAesMode.Cbc,default,iv,false,false,otp,cancellationToken);
            using var first=Transform(input,true,MtkAesMode.Cbc,wrapped.Memory.Span,iv,cancellationToken);
            return Transform(first.Memory.Span,true,MtkAesMode.Cbc,wrapped.Memory.Span,iv,cancellationToken);
        }
        finally{CryptographicOperations.ZeroMemory(input);CryptographicOperations.ZeroMemory(iv);}
    }
    /// <summary>Normal V3 user profiles 0/1/3. User 2 accepts a caller-supplied 256-bit wrapped key.</summary>
    public MtkSensitiveBuffer TransformForUser(ReadOnlySpan<byte> data,bool encrypt,int user,
        ReadOnlySpan<byte> wrappedKey=default,ReadOnlySpan<byte> otp=default,CancellationToken cancellationToken=default)
    {
        if(user is <0 or >3 || (user==2?wrappedKey.Length!=32:!wrappedKey.IsEmpty))throw new ArgumentOutOfRangeException(nameof(user));
        if(user==2)return TransformCore(data,encrypt,MtkAesMode.Cbc,wrappedKey,Convert.FromHexString("57325A5A125497661254976657325A5A"),false,false,otp,cancellationToken);
        ReadOnlySpan<uint> words=user switch {0=>DefaultIv,1=>[0xAA542CDA,0x55522114,0xE3F083BD,0x55522114],_=>[0x2684B690,0xEB67A8BE,0xA113144C,0x177B1215]};
        Span<byte> iv=stackalloc byte[16];for(int i=0;i<4;i++)BinaryPrimitives.WriteUInt32LittleEndian(iv[(i*4)..],words[i]);
        try{return TransformV3(data,encrypt,iv,otp:otp,cancellationToken:cancellationToken);}finally{CryptographicOperations.ZeroMemory(iv);}
    }
}
