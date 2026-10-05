// SPDX-License-Identifier: AGPL-3.0-or-later
// SecRpmbInfo layout facts: hacc e5a68124, Shomy/rva3 2026 (MIT); normal flow: penumbra-main (AGPL).
using System.Buffers.Binary;
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

public sealed partial class MtkDaExtension
{
    private void RpmbLockReady(IMtkDaChannel channel)
    {
        Ready(channel);if(channel.Kind!=MtkDaKind.Xml || channel.Storage.Kind!=MtkStorageKind.Ufs)throw new MtkCapabilityException("XML/UFS RPMB lock metadata");
        _ = RpmbRange(channel,1,0,1);
    }
    private static MtkRpmbLockInfo DecodeRpmbLock(ReadOnlySpan<byte> data)
    {
        if(data.Length!=256 || BinaryPrimitives.ReadUInt32LittleEndian(data)!=0x52534543 || BinaryPrimitives.ReadUInt32LittleEndian(data[252..])!=0x43455352 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..])!=1 || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) is <1 or >6)throw new MtkResourceException("RPMB lock metadata");
        return new(1,(MtkDeviceLockState)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
    }
    private static void ReadRpmbLockBlock(IMtkDaChannel c,byte[] bytes)
    {
        c.BeginXmlCommand("EXT-RPMB-READ",RpmbArgs(1,0,1));using var output=new MemoryStream(bytes,true);c.ReceiveXmlFile(output,256,256);c.EndXmlCommand();
    }
    /// <summary>Reads authenticated XML/UFS R1 block-zero lock metadata; no key derivation or programming is implicit.</summary>
    public MtkRpmbLockInfo ReadRpmbLockState(CancellationToken cancellationToken=default)=>_access.UseSession(c=>
    {
        RpmbLockReady(c);byte[] bytes=new byte[256];try{ReadRpmbLockBlock(c,bytes);return DecodeRpmbLock(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}
    },cancellationToken);
    /// <summary>Persists the original block before changing only the lock state, then compares all 256 readback bytes.
    /// Backup is borrowed; a FileStream is durably flushed. Failure after writing is never retried.</summary>
    public void SetRpmbLockState(bool locked,Stream backup,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(backup);if(!backup.CanWrite)throw new ArgumentException(nameof(backup));
        _access.UseSession(c=>
        {
            RpmbLockReady(c);byte[] original=new byte[256],replacement=new byte[256];bool writing=false;
            try
            {
                ReadRpmbLockBlock(c,original);_ = DecodeRpmbLock(original);backup.Write(original);
                if(backup is FileStream file)file.Flush(true);else backup.Flush();
                original.CopyTo(replacement,0);BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(8),locked?4u:3u);
                if(CryptographicOperations.FixedTimeEquals(original,replacement))return 0;
                writing=true;c.BeginXmlCommand("EXT-RPMB-WRITE",RpmbArgs(1,0,1));using(var input=new MemoryStream(replacement,false))c.SendXmlFile(input,256);c.EndXmlCommand();
                ReadRpmbLockBlock(c,original);if(!CryptographicOperations.FixedTimeEquals(original,replacement))throw new MtkResourceException("RPMB lock readback");return 0;
            }
            catch(Exception ex)when(writing){try{c.Invalidate();}catch{}throw new MtkRpmbLockWriteException(ex);}
            finally{CryptographicOperations.ZeroMemory(original);CryptographicOperations.ZeroMemory(replacement);}
        },cancellationToken);
    }
}
