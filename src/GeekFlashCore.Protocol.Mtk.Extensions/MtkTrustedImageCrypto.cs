// SPDX-License-Identifier: AGPL-3.0-or-later
// Public MTEE CBC/descramble format: B. Kerler, mtkclient hwcrypto_dxcc.py, GPLv3.
using System.Buffers;
using System.Security.Cryptography;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Streaming public MTEE image formats. Input and output streams remain caller-owned.</summary>
public static class MtkTrustedImageCrypto
{
    private const int Window=65536;
    private static void Validate(Stream source,Stream destination,long length)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(destination);
        if(!source.CanRead || !destination.CanWrite || ReferenceEquals(source,destination))throw new ArgumentException(nameof(source));
        if(length<0)throw new ArgumentOutOfRangeException(nameof(length));
    }
    /// <summary>Decrypts exactly length bytes of the public AES-CBC MTEE format, without padding.</summary>
    public static void DecryptMtee(Stream source,Stream destination,long length,CancellationToken cancellationToken=default)
    {
        Validate(source,destination,length);if(length%16!=0)throw new ArgumentOutOfRangeException(nameof(length));cancellationToken.ThrowIfCancellationRequested();
        byte[] seed=Convert.FromHexString("B936C14D95A99585073E5607784A51F7444B60D6BFD6110F76D004CCB7E1950E"),material=SHA256.HashData(seed);
        byte[] input=ArrayPool<byte>.Shared.Rent(Window),output=ArrayPool<byte>.Shared.Rent(Window);
        try
        {
            using var aes=Aes.Create();aes.Padding=PaddingMode.None;aes.Mode=CipherMode.CBC;aes.Key=material.AsSpan(0,16).ToArray();aes.IV=material.AsSpan(16,16).ToArray();
            using var transform=aes.CreateDecryptor();
            for(long done=0;done<length;)
            {
                cancellationToken.ThrowIfCancellationRequested();int count=(int)Math.Min(Window,length-done);source.ReadExactly(input.AsSpan(0,count));
                int written=transform.TransformBlock(input,0,count,output,0);destination.Write(output.AsSpan(0,written));done+=count;
            }
            byte[] final=transform.TransformFinalBlock([],0,0);try{destination.Write(final);}finally{CryptographicOperations.ZeroMemory(final);}cancellationToken.ThrowIfCancellationRequested();
        }
        finally {CryptographicOperations.ZeroMemory(seed);CryptographicOperations.ZeroMemory(material);ArrayPool<byte>.Shared.Return(input,true);ArrayPool<byte>.Shared.Return(output,true);}
    }
    /// <summary>Decodes exactly length bytes using the public AES-CTR descramble format and its big-endian counter.</summary>
    public static void Descramble(Stream source,Stream destination,long length,CancellationToken cancellationToken=default)
    {
        Validate(source,destination,length);cancellationToken.ThrowIfCancellationRequested();
        byte[] counter=Convert.FromHexString("A0B0924686447109F2D51DCDDC93458A"),key=Convert.FromHexString("5C0E349A27DC46034C7B6744A378BD17");
        byte[] input=ArrayPool<byte>.Shared.Rent(Window),counters=ArrayPool<byte>.Shared.Rent(Window),mask=ArrayPool<byte>.Shared.Rent(Window);
        try
        {
            using var aes=Aes.Create();aes.Padding=PaddingMode.None;aes.Mode=CipherMode.ECB;aes.Key=key;using var transform=aes.CreateEncryptor();
            for(long done=0;done<length;)
            {
                cancellationToken.ThrowIfCancellationRequested();int count=(int)Math.Min(Window,length-done);source.ReadExactly(input.AsSpan(0,count));int aligned=(count+15)&~15;
                for(int block=0;block<aligned;block+=16)
                {
                    counter.CopyTo(counters,block);for(int i=15;i>=0;i--){counter[i]++;if(counter[i]!=0)break;}
                }
                transform.TransformBlock(counters,0,aligned,mask,0);for(int i=0;i<count;i++)input[i]^=mask[i];destination.Write(input.AsSpan(0,count));done+=count;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally {CryptographicOperations.ZeroMemory(counter);CryptographicOperations.ZeroMemory(key);ArrayPool<byte>.Shared.Return(input,true);ArrayPool<byte>.Shared.Return(counters,true);ArrayPool<byte>.Shared.Return(mask,true);}
    }
}
