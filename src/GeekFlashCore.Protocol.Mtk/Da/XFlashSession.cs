// SPDX-License-Identifier: AGPL-3.0-or-later
// Wire order derived from penumbra (Shomy 2025-2026, AGPL-3.0-or-later)
// and mtkclient (B. Kerler 2018-2024, GPLv3); bounded synchronous rewrite.
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Internals;
using GeekFlashCore.Protocol.Mtk.Loaders;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal sealed class XFlashSession(MtkWire wire, MtkProtocolOptions options) : IMtkDaSession
{
    public MtkDaKind Kind => MtkDaKind.XFlash;
    public void Command(uint command)
    {
        wire.Command = command;
        wire.SendFrame(MtkWire.Le32(command));
        wire.ReadStatus();
    }
    public void Parameters(params byte[][] data)
    {
        foreach (var part in data)
            wire.SendFrame(part);
        wire.ReadStatus();
    }
    public byte[] Control(uint command, int maximum = 512)
    {
        Command(0x10009);
        Command(command);
        byte[] result = wire.ReadSmallFrame(Math.Min(maximum, options.MaximumFrameSize));
        try
        {
            wire.ReadStatus();
            return result;
        }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(result); throw; }
    }
    public void Control(uint command, params byte[][] data)
    {
        Command(0x10009);
        Command(command);
        Parameters(data);
    }
    public void Initialize(MtkDaImage image, MtkEmiImage? emi, MtkTargetInfo target,
        Func<MtkExploitStage, MtkDaImage> checkpoint)
    {
        if (wire.ReadByte() != 0xc0)
            throw wire.Failure();
        wire.Stage = MtkBootStage.Da1;
        wire.SendFrame(MtkWire.Le32(0x434e5953));
        byte[] environment = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(environment, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(environment.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(environment.AsSpan(8), 1);
        Parameters(MtkWire.Le32(0x10100), environment);
        Parameters(MtkWire.Le32(0x10101), new byte[4]);
        wire.ReadStatus(0x434e5953);
        byte[] agent = Control(0x4000a);
        if (!agent.AsSpan().SequenceEqual("preloader"u8) && !agent.AsSpan().SequenceEqual("brom"u8))
            throw new MtkResourceException("connection agent");
        if (agent.AsSpan().SequenceEqual("brom"u8))
        {
            if (emi is null)
                throw new MtkResourceException("EMI");
            if (emi.Source.Length <= 0 || emi.Source.Length > options.MaximumFrameSize)
                throw new MtkResourceException("EMI size");
            using Stream emiStream = emi.Source.OpenStream();
            byte[] bytes = new byte[(int)emi.Source.Length];
            emiStream.ReadExactly(bytes);
            Command(0x1000a);
            Parameters(MtkWire.Le32((uint)bytes.Length), bytes);
        }
        Control(0x20003, new byte[4]);
        QueryPacketLength();
        image = checkpoint(MtkExploitStage.Da1Ready);
        var region = image.Entry.Regions[image.Entry.EntryRegionIndex + 1];
        long length = region.Length - region.SignatureLength;
        using Stream source = new MtkDataWindow(image.Source, region.FileOffset, length).OpenStream();
        Command(0x10008);
        byte[] range = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(range, region.Address);
        BinaryPrimitives.WriteUInt64LittleEndian(range.AsSpan(8), (ulong)length);
        wire.SendFrame(range);
        SendStreamFrame(source, length);
        wire.ReadStatus();
        wire.ReadStatus(0, 0x434e5953);
        wire.Stage = MtkBootStage.Da2;
        QueryPacketLength();
        checkpoint(MtkExploitStage.Da2Ready);
    }
    public byte[]? GetAuthenticationChallenge()
    {
        byte[] state = Control(0x40016);
        if (state.Length != 4)
            throw wire.Failure();
        if (BinaryPrimitives.ReadUInt32LittleEndian(state) == 0)
            return null;
        byte[] challenge = Control(0x40013, maximum: options.MaximumFrameSize);
        if (challenge.Length < 20)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(challenge);
            throw wire.Failure();
        }
        return challenge;
    }
    public void Authenticate(ReadOnlySpan<byte> response)
    {
        byte[] copy = response.ToArray();
        try
        {
            Control(0x2000b, copy);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(copy); }
    }
    private void QueryPacketLength()
    {
        byte[] packet = Control(0x40007);
        if (packet.Length != 8)
            throw wire.Failure();
        uint write = BinaryPrimitives.ReadUInt32LittleEndian(packet), read = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
        if (write is < 512 or > 1048576 || read is < 512 or > 1048576)
            throw wire.Failure();
        wire.WritePacketLength = (int)Math.Min(write, (uint)options.BufferSize);
    }
    public MtkStorageInfo GetStorage()
    {
        byte[] emmc = Control(0x40001);
        if (emmc.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(emmc) is 1 or 2)
            return MtkStorageDecoder.Emmc(emmc);
        if (emmc.Any(b => b != 0))
            throw new MtkResourceException("eMMC info");
        byte[] ufs = Control(0x40004);
        if (ufs.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(ufs) == 0x30)
            return MtkStorageDecoder.Ufs(ufs);
        if (ufs.Any(b => b != 0)) throw new MtkResourceException("UFS info");
        byte[] nand = Control(0x40002);
        if (nand.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(nand) != 0)
            return MtkStorageDecoder.Nand(nand, options.EnableNandLogicalWrites);
        if (nand.Any(b => b != 0)) throw new MtkResourceException("NAND info");
        return MtkStorageDecoder.Nor(Control(0x40003), options.NorEraseBlockSize);
    }
    public void Read(MtkStorageRegion region, long offset, long length, Stream output)
    {
        Command(0x10005);
        Parameters(FlashParams(region, offset, length));
        wire.ReadStatus();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(options.MaximumFrameSize);
        try
        {
            long done = 0;
            while (done < length)
            {
                int n = wire.ReadFrame(buffer.AsSpan(0, (int)Math.Min(options.MaximumFrameSize, length - done)));
                output.Write(buffer.AsSpan(0, n));
                done += n;
                wire.SendFrame(new byte[4]);
                wire.ReadStatus();
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    public void Write(MtkStorageRegion region, long offset, long length, Stream input)
    {
        if (!region.CanWrite) throw new MtkCapabilityException("NAND logical writes");
        Command(0x10004);
        Parameters(FlashParams(region, offset, length));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(wire.WritePacketLength);
        try
        {
            for (long done = 0; done < length;)
            {
                wire.Check();
                int n = (int)Math.Min(wire.WritePacketLength, length - done);
                input.ReadExactly(buffer.AsSpan(0, n));
                wire.SendFrame(new byte[4]);
                wire.SendFrame(MtkWire.Le32(MtkWire.Sum(buffer.AsSpan(0, n))));
                wire.SendFrame(buffer.AsSpan(0, n));
                wire.ReadStatus();
                done += n;
            }
            wire.ReadStatus();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    public void Erase(MtkStorageRegion region, long offset, long length)
    {
        if (!region.CanWrite || region.EraseBlockSize == 0)
            throw new MtkCapabilityException("erase geometry/policy");
        if (offset % region.EraseBlockSize != 0 || length % region.EraseBlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        Command(0x10003);
        Parameters(FlashParams(region, offset, length, erase: true));
        Span<byte> delay = stackalloc byte[4];
        for (int i = 0; i < options.MaximumProgressEvents; i++)
        {
            uint status = wire.ReadStatus(0x40040004, 0x40040005);
            if (status == 0x40040005)
                return;
            if (wire.ReadFrame(delay) != 4)
                throw wire.Failure();
            uint milliseconds = BinaryPrimitives.ReadUInt32LittleEndian(delay);
            if (milliseconds > 3000)
                throw wire.Failure();
            wire.Check();
            if (milliseconds > 0 && wire.Token.WaitHandle.WaitOne((int)milliseconds))
                wire.Token.ThrowIfCancellationRequested();
            wire.SendFrame(new byte[4]);
        }
        throw wire.Failure();
    }
    public void Reboot(ProtocolRebootMode mode)
    {
        Command(0x10007);
        byte[] p = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(p, mode == ProtocolRebootMode.PowerOff ? 0u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), mode == ProtocolRebootMode.Download ? 2u : 0u);
        Parameters(p);
    }
    public byte[] ReadEfuses()
    {
        Command(0x1000f);Parameters(new byte[0xf8]);byte[] result=wire.ReadSmallFrame(Math.Min(options.MaximumFrameSize,0x5000));
        try { wire.SendFrame(new byte[4]);wire.ReadStatus();return result; }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(result);throw; }
    }
    public void WriteEfuses(ReadOnlySpan<byte> data)
    {
        Command(0x1000e);wire.SendFrame(data);wire.SendFrame(new byte[0xf8]);wire.ReadStatus();wire.ReadStatus();
    }
    private void DownloadInfo(uint action) { Command(0x10009);Command(action);wire.ReadStatus(); }
    public long ReadNamed(string name,Stream destination,long maximum)
    {
        Command(0x10002);Parameters(System.Text.Encoding.ASCII.GetBytes(name));byte[] bytes=wire.ReadSmallFrame(8);wire.ReadStatus();
        if(bytes.Length!=8)throw new MtkResourceException("partition upload size");ulong size=BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if(size==0 || size>(ulong)maximum)throw new MtkResourceException("partition upload limit");
        byte[] buffer=ArrayPool<byte>.Shared.Rent(options.MaximumFrameSize);
        try
        {
            for(long done=0;done<(long)size;)
            {
                int n=wire.ReadFrame(buffer.AsSpan(0,(int)Math.Min(options.MaximumFrameSize,(long)size-done)));if(n==0)throw wire.Failure();destination.Write(buffer.AsSpan(0,n));wire.SendFrame(new byte[4]);wire.ReadStatus();done+=n;
            }
            return (long)size;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer,true); }
    }
    public void WriteNamed(string name,Stream source,long length)
    {
        DownloadInfo(0x80001);Command(0x10001);byte[] size=new byte[8];BinaryPrimitives.WriteUInt64LittleEndian(size,(ulong)length);Parameters(System.Text.Encoding.ASCII.GetBytes(name),size);
        byte[] buffer=ArrayPool<byte>.Shared.Rent(wire.WritePacketLength);
        try
        {
            for(long done=0;done<length;)
            { wire.Check();int n=(int)Math.Min(wire.WritePacketLength,length-done);source.ReadExactly(buffer.AsSpan(0,n));wire.SendFrame(new byte[4]);wire.SendFrame(MtkWire.Le32(MtkWire.Sum(buffer.AsSpan(0,n))));wire.SendFrame(buffer.AsSpan(0,n));wire.ReadStatus();done+=n; }
            wire.ReadStatus();DownloadInfo(0x80002);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer,true); }
    }
    public void EraseNamed(string name)
    {
        DownloadInfo(0x80001);Command(0x10006);wire.SendFrame(System.Text.Encoding.ASCII.GetBytes(name));
        for(int i=0;i<options.MaximumProgressEvents;i++)
        {
            uint status=wire.ReadStatus(0x40040004,0x40040005);if(status==0x40040005) { DownloadInfo(0x80002);return; }
            byte[] percent=wire.ReadSmallFrame(4);if(percent.Length!=4 || BinaryPrimitives.ReadUInt32LittleEndian(percent)>100)throw wire.Failure();
            wire.SendFrame(new byte[4]);wire.ProgressPercent?.Invoke((int)BinaryPrimitives.ReadUInt32LittleEndian(percent));
        }
        throw wire.Failure();
    }
    private void SendStreamFrame(Stream source, long length)
    {
        wire.SendFrameHeader(length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(wire.WritePacketLength);
        try
        {
            while (length > 0)
            {
                wire.Check();
                int n = (int)Math.Min(wire.WritePacketLength, length);
                source.ReadExactly(buffer.AsSpan(0, n));
                wire.Write(buffer.AsSpan(0, n));
                length -= n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    private static byte[] FlashParams(MtkStorageRegion region, long offset, long length, bool erase = false)
    {
        byte[] p = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)region.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), region.WireId);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(8), (ulong)offset);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(16), (ulong)length);
        if (region.Kind == MtkStorageKind.Nand && !erase)
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(36), 2); // Logical data pages with ECC, excluding OOB.
        return p;
    }
}
