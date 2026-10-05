// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard Legacy sequence: B. Kerler, bkerler/mtkclient, 2018-2024, GPLv3.
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Internals;
using GeekFlashCore.Protocol.Mtk.Loaders;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal sealed class LegacySession(MtkWire wire, MtkProtocolOptions options) : IMtkDaSession
{
    private MtkStorageInfo? _storage;
    public MtkDaKind Kind => MtkDaKind.Legacy;
    private void Ack(byte expected = 0x5a)
    {
        byte actual = wire.ReadByte();
        if (actual != expected)
            throw wire.Failure(actual);
    }
    private void Write32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        wire.Write(b);
    }
    private void Discard(int count)
    {
        Span<byte> buffer = stackalloc byte[256];
        while (count > 0)
        {
            int n = Math.Min(buffer.Length, count);
            wire.Read(buffer[..n]);
            count -= n;
        }
    }
    public void Initialize(MtkDaImage image, MtkEmiImage? emi, MtkTargetInfo target,
        Func<MtkExploitStage, MtkDaImage> checkpoint)
    {
        if (wire.ReadByte() != 0xc0)
            throw wire.Failure();
        wire.Stage = MtkBootStage.Da1;
        _ = wire.Read32();
        ushort nandCount = wire.Read16();
        if (nandCount > 256)
            throw wire.Failure();
        Discard(nandCount * 2);
        _ = wire.Read32();
        Span<byte> ids = stackalloc byte[16];
        wire.Read(ids);
        if (ids.IndexOfAnyExcept((byte)0) < 0)
            throw new MtkCapabilityException("Legacy NAND/NOR");
        wire.WriteByte(0x5a);
        Discard(3);
        // Known common Legacy configuration. Chip-specific trailing fields stay explicit.
        using var config = new MemoryStream();
        config.WriteByte(target.BromVersion);
        config.WriteByte(target.PreloaderVersion);
        config.Write([0, 8, 0, 0x70, 7, 0xff, 0xff, 0, 0, 0, 0, 0, 1]);
        config.WriteByte(target.HardwareCode == 0x6583 ? (byte)0 : (byte)1);
        config.Write([2, 0]);
        switch (target.HardwareCode)
        {
            case 0x6582:
                config.Write([0, 0, 0, 1]);
                break;
            case 0x6583:
                config.Write([0, 0, 0, 0]);
                break;
            case 0x6589:
                config.Write([0, 0, 0, 1]);
                break;
            case 0x6592:
                config.Write([0, 0, 0, 0]);
                break;
            case 0x8127:
                config.Write([0, 0, 0, 0]);
                goto case 0x6580;
            case 0x6580:
            case 0x8163:
                config.Write([0, 0, 0, 1, 0x46, 0x46, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0, 0, 0]);
                break;
        }
        wire.Write(config.GetBuffer().AsSpan(0, (int)config.Length));
        uint dramStatus = wire.Read32();
        if (dramStatus == 0 && target.HardwareCode == 0x6592)
            Discard(20);
        else if (dramStatus == 0xbc3)
            InitializeEmi(emi, target);
        else if (dramStatus != 0)
            throw wire.Failure(dramStatus);
        image = checkpoint(MtkExploitStage.Da1Ready);
        var region = image.Entry.Regions[image.Entry.EntryRegionIndex + 1];
        using Stream source = new MtkDataWindow(image.Source, region.FileOffset, region.Length).OpenStream();
        Write32(region.Address);
        Write32(region.Length);
        Write32(0x1000);
        Ack();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            long left = region.Length;
            while (left > 0)
            {
                wire.Check();
                int n = (int)Math.Min(4096, left);
                source.ReadExactly(buffer.AsSpan(0, n));
                wire.Write(buffer.AsSpan(0, n));
                Ack();
                left -= n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        wire.WriteByte(0x5a);
        Ack();
        wire.Stage = MtkBootStage.Da2;
        Discard(0x1c);
        Span<byte> nand = stackalloc byte[0x11];
        wire.Read(nand);
        int count = BinaryPrimitives.ReadUInt16BigEndian(nand[15..]);
        if (count > 256)
            throw wire.Failure();
        if (count == 0)
        {
            // 32-bit NAND geometry puts count at offset 11 and already supplies four ID bytes.
            count = BinaryPrimitives.ReadUInt16BigEndian(nand[11..]);
            if (count < 2 || count > 256)
                throw new MtkCapabilityException("Legacy NAND layout");
            Discard(count * 2 - 4);
        }
        else
            Discard(count * 2);
        Discard(9);
        Span<byte> emmc = stackalloc byte[0x5c];
        wire.Read(emmc);
        _storage = MtkStorageDecoder.Emmc(emmc, true);
        Discard(0x1c + 0x26);
        if (target.HardwareCode == 0x8163)
            Discard(4);
        Span<byte> pass = stackalloc byte[10];
        wire.Read(pass);
        if (pass[0] != 0x5a)
            throw wire.Failure();
        CheckUsbSpeed(); // No automatic speed switch or reset.
        checkpoint(MtkExploitStage.Da2Ready);
    }
    private void InitializeEmi(MtkEmiImage? emi, MtkTargetInfo target)
    {
        Discard(4 + 16);
        if (wire.Read32() != 0xbc4)
            throw wire.Failure();
        ushort count = wire.Read16();
        if (count > 256)
            throw wire.Failure();
        Discard(count * 2);
        if (emi is null || emi.Version is not (0 or 0x0b or 0x0c or 0x0d or 0x0f or 0x10 or 0x11 or 0x14 or 0x15) ||
            emi.Source.Length is <= 0 or > 1048576)
            throw new MtkResourceException("Legacy EMI");
        wire.WriteByte(0xe8);
        Write32(emi.Version == 0 ? uint.MaxValue : emi.Version);
        Ack();
        if (emi.Version == 0x0b)
            Discard(16);
        uint required = wire.Read32();
        if (required == 0 || required > emi.Source.Length)
            throw wire.Failure();
        wire.WriteByte(0x5a);
        bool truncate = emi.Version is 0 or 0x0c or 0x0d;
        int size = checked((int)(truncate ? required : emi.Source.Length));
        if (emi.Version is 0 or 0x0f or 0x10 or 0x11 or 0x14 or 0x15 && target.HardwareCode != 0x8127)
            Write32((uint)size);
        byte[] data = new byte[size];
        using (Stream stream = emi.Source.OpenStream())
            stream.ReadExactly(data);
        if (emi.Version is 0x0c or 0x0d)
            BinaryPrimitives.WriteUInt32BigEndian(data, 0x100);
        wire.Write(data);
        _ = wire.Read16(); // Legacy EMI checksum algorithm is not specified by the reference.
        wire.WriteByte(0x5a);
        Write32(0x80000001);
        uint status = wire.Read32();
        if (status != 0)
            throw wire.Failure(status);
        Discard(10);
        if (emi.Version == 0x0d)
            Discard(20);
    }
    public byte[]? GetAuthenticationChallenge() => null; // Authentication completed before Legacy DA1.
    public void Authenticate(ReadOnlySpan<byte> response) => throw new MtkCapabilityException("Legacy DA SLA");
    public MtkStorageInfo GetStorage() => _storage ?? throw new MtkResourceException("Legacy storage");
    private void CheckUsbSpeed()
    {
        wire.WriteByte(0x72);
        Ack();
        _ = wire.ReadByte();
    }
    private void Switch(MtkStorageRegion region)
    {
        wire.WriteByte(0x60);
        Ack();
        wire.WriteByte(checked((byte)region.WireId));
        Ack();
    }
    private void Header(byte command, MtkStorageRegion region, long offset, long length, bool write)
    {
        wire.Command = command;
        wire.WriteByte(command);
        if (write)
            wire.WriteByte((byte)region.Kind);
        else
            wire.Write([0x0c, 2]);
        if (write)
            wire.WriteByte((byte)region.WireId);
        Span<byte> p = stackalloc byte[20];
        BinaryPrimitives.WriteUInt64BigEndian(p, (ulong)offset);
        BinaryPrimitives.WriteUInt64BigEndian(p[8..], (ulong)length);
        BinaryPrimitives.WriteUInt32BigEndian(p[16..], (uint)options.BufferSize);
        wire.Write(p);
        Ack();
    }
    public void Read(MtkStorageRegion region, long offset, long length, Stream output)
    {
        CheckUsbSpeed();
        Switch(region);
        Header(0xd6, region, offset, length, false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
        try
        {
            while (length > 0)
            {
                int n = (int)Math.Min(options.BufferSize, length);
                wire.Read(buffer.AsSpan(0, n));
                ushort sum = wire.Read16();
                if (sum != MtkWire.Sum(buffer.AsSpan(0, n)))
                    throw wire.Failure(sum);
                output.Write(buffer.AsSpan(0, n));
                wire.WriteByte(0x5a);
                length -= n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    public void Write(MtkStorageRegion region, long offset, long length, Stream input)
    {
        Header(0x62, region, offset, length, true);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
        Span<byte> b = stackalloc byte[2];
        try
        {
            while (length > 0)
            {
                wire.Check();
                int n = (int)Math.Min(options.BufferSize, length);
                input.ReadExactly(buffer.AsSpan(0, n));
                wire.WriteByte(0x5a);
                wire.Write(buffer.AsSpan(0, n));
                ushort sum = MtkWire.Sum(buffer.AsSpan(0, n));
                BinaryPrimitives.WriteUInt16BigEndian(b, sum);
                wire.Write(b);
                Ack(0x69);
                length -= n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    public void Erase(MtkStorageRegion region, long offset, long length)
    {
        CheckUsbSpeed();
        Switch(region);
        wire.Write([0xd4, 2, 0, 0, 0]);
        Span<byte> p = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(p, (ulong)offset);
        BinaryPrimitives.WriteUInt64BigEndian(p[8..], (ulong)length);
        wire.Write(p);
        for (int i = 0; i < options.MaximumProgressEvents; i++)
        {
            Ack();
            Ack();
            Discard(4);
            byte percent = wire.ReadByte();
            if (percent > 100)
                throw wire.Failure();
            wire.WriteByte(0x5a);
            if (percent == 100)
            {
                Ack();
                Ack();
                return;
            }
        }
        throw wire.Failure();
    }
    public void Reboot(ProtocolRebootMode mode)
    {
        if (mode != ProtocolRebootMode.System)
            throw new MtkCapabilityException("Legacy reboot mode");
        wire.WriteByte(0xd9);
        Ack();
        Write32(0);
        Ack();
    }
}
