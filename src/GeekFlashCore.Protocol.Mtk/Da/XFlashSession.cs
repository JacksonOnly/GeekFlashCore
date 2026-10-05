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
    public byte[] Control(uint command)
    {
        Command(0x10009);
        Command(command);
        byte[] result = wire.ReadSmallFrame(512);
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
        if (!agent.AsSpan().SequenceEqual("preloader"u8))
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
        BinaryPrimitives.WriteUInt64LittleEndian(range, checked(region.Address + region.EntryOffset));
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
        byte[] challenge = Control(0x40013);
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
        if (emmc.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(emmc) == 1)
            return MtkStorageDecoder.Emmc(emmc);
        if (emmc.Any(b => b != 0))
            throw new MtkResourceException("eMMC info");
        return MtkStorageDecoder.Ufs(Control(0x40004));
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
        Command(0x10003);
        Parameters(FlashParams(region, offset, length));
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
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16), mode == ProtocolRebootMode.Download ? 1u : 0u);
        Parameters(p);
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
    private static byte[] FlashParams(MtkStorageRegion region, long offset, long length)
    {
        byte[] p = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)region.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), region.WireId);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(8), (ulong)offset);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(16), (ulong)length);
        return p;
    }
}
