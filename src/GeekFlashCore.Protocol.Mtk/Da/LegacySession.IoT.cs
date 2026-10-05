// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard MT6261 DA3 configuration: B. Kerler, mtkclient dalegacy_lib.py, GPLv3.
using GeekFlashCore.Protocol.Mtk.Loaders;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal sealed partial class LegacySession
{
    private void InitializeIoT(MtkDaImage image,Func<MtkExploitStage,MtkDaImage> checkpoint)
    {
        Discard(3);image=checkpoint(MtkExploitStage.Da1Ready);
        wire.Write([0xa5,0x05,0xfe,0,8,0,0x70,7,0xff,0xff,2,0,0,1,3]);Ack();
        var third=image.Entry.Regions[image.Entry.EntryRegionIndex+2];
        using(Stream source=new MtkDataWindow(image.Source,third.FileOffset,0x1d4).OpenStream())
        {
            Span<byte> buffer=stackalloc byte[0x24];
            for(int left=0x1d4;left>0;)
            {
                int n=Math.Min(buffer.Length,left);source.ReadExactly(buffer[..n]);wire.Write(buffer[..n]);left-=n;if(left>0)Ack(0x69);
            }
        }
        Ack();Ack(0xa5);Write32(0);Discard(4);wire.Stage=MtkBootStage.Da2;
        Span<byte> nor=stackalloc byte[54],nand=stackalloc byte[35],emmc=stackalloc byte[44];wire.Read(nor);wire.Read(nand);wire.Read(emmc);Discard(30);
        Ack();Ack();uint status=wire.Read32();_ = wire.Read32();if(status!=0)throw wire.Failure(status);Ack(0xc1);
        wire.WriteByte(0x59);Ack(0xa5);wire.WriteByte(0xf0);ReadFatInfo();
        ulong nandSize=BinaryPrimitives.ReadUInt32BigEndian(nand[7..]);
        if(BinaryPrimitives.ReadUInt32BigEndian(nand)==0 && nandSize>0)
            _storage=DecodeNand(nandSize,BinaryPrimitives.ReadUInt16LittleEndian(nand[27..]),BinaryPrimitives.ReadUInt16LittleEndian(nand[29..]),BinaryPrimitives.ReadUInt16LittleEndian(nand[31..]),0);
        else if(BinaryPrimitives.ReadUInt32BigEndian(emmc)==0 && BinaryPrimitives.ReadUInt32BigEndian(emmc[40..])>0)
        {
            byte[] ordinary=new byte[92];for(int i=0;i<8;i++)BinaryPrimitives.WriteUInt64BigEndian(ordinary.AsSpan(4+i*8),BinaryPrimitives.ReadUInt32BigEndian(emmc[(12+i*4)..]));
            _storage=MtkStorageDecoder.Emmc(ordinary,true);
        }
        else if(BinaryPrimitives.ReadUInt32BigEndian(nor)==0 && BinaryPrimitives.ReadUInt32BigEndian(nor[8..])>0)
            _storage=MtkStorageDecoder.Single(MtkStorageKind.Nor,BinaryPrimitives.ReadUInt32BigEndian(nor[8..]),1,options.NorEraseBlockSize);
        else throw new MtkResourceException("IoT storage");
        wire.WriteByte(0x59);Ack();wire.WriteByte(0xf0);ReadFatInfo();wire.Write([0xd2,1,1]);Ack();wire.WriteByte(0x5a);wire.ConfigureIoTCdc();
        bool synchronized=false;
        for(int i=0;i<10;i++) { wire.WriteByte(0xc0);if(wire.ReadByte()==0xc0) { synchronized=true;break; } }
        if(!synchronized)throw wire.Failure();wire.WriteByte(0x5a);Ack();
        for(int i=0;i<256;i++)wire.EchoByte((byte)i);
        checkpoint(MtkExploitStage.Da2Ready);
    }
    private void ReadFatInfo()
    {
        uint status=wire.Read32();if(status!=0)throw wire.Failure(status);Discard(24);
    }
}
