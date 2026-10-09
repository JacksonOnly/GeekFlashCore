// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard Legacy sequence: B. Kerler, bkerler/mtkclient, 2018-2024, GPLv3.
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Internals;
using GeekFlashCore.Protocol.Mtk.Loaders;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal sealed partial class LegacySession(MtkWire wire, MtkProtocolOptions options) : IMtkDaSession
{
    private MtkStorageInfo? _storage;
    public MtkDaKind Kind => MtkDaKind.Legacy;
    private void SendCommand(MtkLegacyCommand command)
    {
        wire.TraceCommand((byte)command, command.ToString());
        wire.WriteByte((byte)command);
    }
    private void Ack(MtkLegacyResponse expected = MtkLegacyResponse.Ack) => Ack((byte)expected);
    private void Ack(byte expected)
    {
        byte actual = wire.ReadByte();
        wire.TraceStatus(actual, actual == expected);
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
        if (wire.ReadByte() != (byte)MtkLegacyResponse.Sync)
            throw wire.Failure();
        wire.Stage = MtkBootStage.Da1;
        if(options.LegacyIoT) { InitializeIoT(image,checkpoint);return; }
        _ = wire.Read32();
        ushort nandCount = wire.Read16();
        if (nandCount > 256)
            throw wire.Failure();
        Discard(nandCount * 2);
        _ = wire.Read32();
        Span<byte> ids = stackalloc byte[16];
        wire.Read(ids);
        wire.WriteByte((byte)MtkLegacyResponse.Ack);
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
        if (dramStatus == 0) MtkDiagnostics.Summary(wire.Logger, Strings.EmiNotRequired, Kind);
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
        wire.WriteByte((byte)MtkLegacyResponse.Ack);
        Ack();
        wire.Stage = MtkBootStage.Da2;
        Span<byte> nor = stackalloc byte[0x1c];
        wire.Read(nor);
        Span<byte> nand = stackalloc byte[0x11];
        wire.Read(nand);
        int count = BinaryPrimitives.ReadUInt16BigEndian(nand[15..]);
        bool nand32 = count == 0;
        if (count > 256)
            throw wire.Failure();
        if (count == 0)
        {
            // 32-bit NAND geometry puts count at offset 11 and already supplies four ID bytes.
            count = BinaryPrimitives.ReadUInt16BigEndian(nand[11..]);
            if (count==0 && nand.IndexOfAnyExcept((byte)0)<0)
                nand32=false; // An absent NAND descriptor has no device-ID bytes to discard.
            else if (count < 2 || count > 256)
                throw new MtkCapabilityException("Legacy NAND layout");
            if(count>0)Discard(count * 2 - 4);
        }
        else
            Discard(count * 2);
        Span<byte> nandGeometry=stackalloc byte[9];wire.Read(nandGeometry);
        Span<byte> emmc = stackalloc byte[0x5c];
        wire.Read(emmc);
        Span<byte> sdc = stackalloc byte[0x1c];
        wire.Read(sdc);
        ulong nandSize=nand32?BinaryPrimitives.ReadUInt32BigEndian(nand[7..]):BinaryPrimitives.ReadUInt64BigEndian(nand[7..]);
        if(BinaryPrimitives.ReadUInt32BigEndian(nand)==0 && nandSize>0)
            _storage=DecodeNand(nandSize,BinaryPrimitives.ReadUInt16BigEndian(nandGeometry),BinaryPrimitives.ReadUInt16BigEndian(nandGeometry[2..]),BinaryPrimitives.ReadUInt16BigEndian(nandGeometry[4..]),nandGeometry[8]);
        else if (BinaryPrimitives.ReadUInt32BigEndian(emmc) == 0 && BinaryPrimitives.ReadUInt64BigEndian(emmc[60..]) > 0)
            _storage = MtkStorageDecoder.Emmc(emmc, true);
        else if (BinaryPrimitives.ReadUInt32BigEndian(sdc) == 0 && BinaryPrimitives.ReadUInt64BigEndian(sdc[4..]) > 0)
            _storage = MtkStorageDecoder.Single(MtkStorageKind.Sdmmc, BinaryPrimitives.ReadUInt64BigEndian(sdc[4..]), 512);
        else if (BinaryPrimitives.ReadUInt32BigEndian(nor) == 0 && BinaryPrimitives.ReadUInt32BigEndian(nor[8..]) > 0)
            _storage = MtkStorageDecoder.Single(MtkStorageKind.Nor, BinaryPrimitives.ReadUInt32BigEndian(nor[8..]), 1, options.NorEraseBlockSize);
        else throw new MtkCapabilityException("Legacy unknown storage");
        Discard(0x26);
        if (target.HardwareCode == 0x8163)
            Discard(4);
        Span<byte> pass = stackalloc byte[10];
        wire.Read(pass);
        if (pass[0] != (byte)MtkLegacyResponse.Ack)
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
        MtkDiagnostics.Summary(wire.Logger, Strings.EmiStarted, Kind, emi.Source.Length);
        SendCommand(MtkLegacyCommand.InitExtRam);
        Write32(emi.Version == 0 ? uint.MaxValue : emi.Version);
        Ack();
        if (emi.Version == 0x0b)
            Discard(16);
        uint required = wire.Read32();
        if (required == 0 || required > emi.Source.Length)
            throw wire.Failure();
        wire.WriteByte((byte)MtkLegacyResponse.Ack);
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
        wire.WriteByte((byte)MtkLegacyResponse.Ack);
        Write32(0x80000001);
        uint status = wire.Read32();
        if (status != 0)
            throw wire.Failure(status);
        Discard(10);
        if (emi.Version == 0x0d)
            Discard(20);
        wire.Check();
        MtkDiagnostics.Summary(wire.Logger, Strings.EmiCompleted, Kind);
    }
    public byte[]? GetAuthenticationChallenge() => null; // Authentication completed before Legacy DA1.
    public void Authenticate(ReadOnlySpan<byte> response) => throw new MtkCapabilityException("Legacy DA SLA");
    public MtkStorageInfo GetStorage() => _storage ?? throw new MtkResourceException("Legacy storage");
    internal static void ValidateResumeStorage(MtkStorageInfo? storage)
    {
        if (storage?.Regions is not { Count: > 0 and <= 8 } ||
            storage.Regions.Any(r => r is null || r.Kind != storage.Kind) ||
            storage.Regions.Select(r => r.WireId).Distinct().Count() != storage.Regions.Count ||
            !storage.Regions.Any(r => r.WireId == storage.UserRegionId) || storage.Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Sdmmc or MtkStorageKind.Nor))
            throw new MtkResourceException("previously observed Legacy geometry (NAND requires BROM reconnect)");
    }
    internal void Resume(MtkStorageInfo storage)
    {
        ValidateResumeStorage(storage);
        _ = GetUsbSpeed();
        _storage = storage with { Regions = storage.Regions.ToArray() };
    }
    private void CheckUsbSpeed()
    {
        _ = GetUsbSpeed();
    }
    public byte GetUsbSpeed()
    {
        SendCommand(MtkLegacyCommand.GetUsbSpeed);
        Ack();
        return wire.ReadByte();
    }
    public uint ReadRegister(uint address)
    {
        SendCommand(MtkLegacyCommand.ReadRegister); Write32(address);
        uint result = wire.Read32(); Ack(); return result;
    }
    public void WriteRegister(uint address, uint value)
    {
        SendCommand(MtkLegacyCommand.WriteRegister); Write32(address); Write32(value); Ack();
    }
    public byte[] ReadPmt()
    {
        SendCommand(MtkLegacyCommand.ReadPartitionTable); Ack();
        uint length = wire.Read32();
        if (length == 0 || length > 393216) throw new MtkResourceException("PMT length");
        wire.WriteByte((byte)MtkLegacyResponse.Ack);
        byte[] bytes = new byte[(int)length]; wire.Read(bytes); wire.WriteByte((byte)MtkLegacyResponse.Ack); return bytes;
    }
    private void Switch(MtkStorageRegion region)
    {
        SendCommand(MtkLegacyCommand.SwitchPartition);
        Ack();
        wire.WriteByte(checked((byte)region.WireId));
        Ack();
    }
    private void Header(MtkLegacyCommand command, MtkStorageRegion region, long offset, long length, bool write)
    {
        if(!write && options.LegacyIoT && region.Kind==MtkStorageKind.Nor && (offset<0 || length<=0 || (ulong)offset+(ulong)length>uint.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(length));
        SendCommand(command);
        if (write)
            wire.WriteByte((byte)region.Kind);
        else
        {
            if(!options.LegacyIoT || region.Kind!=MtkStorageKind.Nor)wire.WriteByte(0x0c);
            wire.WriteByte(region.Kind == MtkStorageKind.Nor ? (byte)0 : (byte)2);
        }
        if (write)
            wire.WriteByte((byte)region.WireId);
        if(!write && options.LegacyIoT && region.Kind==MtkStorageKind.Nor)
        {
            if(offset<0 || length<=0 || (ulong)offset+(ulong)length>uint.MaxValue)throw new ArgumentOutOfRangeException(nameof(length));
            Span<byte> small=stackalloc byte[12];BinaryPrimitives.WriteUInt32BigEndian(small,(uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(small[4..],(uint)length);BinaryPrimitives.WriteUInt32BigEndian(small[8..],4096);wire.Write(small);Ack();return;
        }
        Span<byte> p = stackalloc byte[20];
        BinaryPrimitives.WriteUInt64BigEndian(p, (ulong)offset);
        BinaryPrimitives.WriteUInt64BigEndian(p[8..], (ulong)length);
        BinaryPrimitives.WriteUInt32BigEndian(p[16..], (uint)options.BufferSize);
        wire.Write(p);
        Ack();
    }
    public void Read(MtkStorageRegion region, long offset, long length, Stream output)
    {
        if(region.Kind==MtkStorageKind.Nand) { ReadNand(offset,length,output,false);return; }
        if (region.Kind == MtkStorageKind.Sdmmc)
            throw new MtkCapabilityException("Legacy SDMMC read");
        wire.TraceStorage(MtkTransferKind.Read, region, offset, length);
        if(!options.LegacyIoT)CheckUsbSpeed();
        if (region.Kind == MtkStorageKind.Emmc) Switch(region);
        Header(MtkLegacyCommand.ReadData, region, offset, length, false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
        try
        {
            while (length > 0)
            {
                int n = (int)Math.Min(options.LegacyIoT && region.Kind==MtkStorageKind.Nor?4096:options.BufferSize, length);
                wire.Read(buffer.AsSpan(0, n));
                ushort sum = wire.Read16();
                bool valid = sum == MtkWire.Sum(buffer.AsSpan(0, n));
                wire.Logger.Debug(Strings.ChecksumValidated, wire.Stage, wire.Command, valid);
                if (!valid)
                    throw wire.Failure();
                output.Write(buffer.AsSpan(0, n));
                wire.WriteByte((byte)MtkLegacyResponse.Ack);
                length -= n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    public void Write(MtkStorageRegion region, long offset, long length, Stream input)
    {
        if(!region.CanWrite)throw new MtkCapabilityException("NAND logical writes");
        wire.TraceStorage(MtkTransferKind.Write, region, offset, length);
        Header(MtkLegacyCommand.WriteData, region, offset, length, true);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
        Span<byte> b = stackalloc byte[2];
        try
        {
            while (length > 0)
            {
                wire.Check();
                int n = (int)Math.Min(options.BufferSize, length);
                input.ReadExactly(buffer.AsSpan(0, n));
                wire.WriteByte((byte)MtkLegacyResponse.Ack);
                wire.Write(buffer.AsSpan(0, n));
                ushort sum = MtkWire.Sum(buffer.AsSpan(0, n));
                BinaryPrimitives.WriteUInt16BigEndian(b, sum);
                wire.Write(b);
                Ack(MtkLegacyResponse.Continue);
                length -= n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    public void Erase(MtkStorageRegion region, long offset, long length)
    {
        if (region.Kind != MtkStorageKind.Emmc)
            throw new MtkCapabilityException("Legacy storage erase");
        wire.TraceStorage(MtkTransferKind.Erase, region, offset, length);
        CheckUsbSpeed();
        Switch(region);
        wire.TraceCommand((byte)MtkLegacyCommand.Format, nameof(MtkLegacyCommand.Format));
        wire.Write([(byte)MtkLegacyCommand.Format, 2, 0, 0, 0]);
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
            wire.WriteByte((byte)MtkLegacyResponse.Ack);
            wire.Logger.Debug(Strings.WireProgress, wire.Stage, wire.Command, percent);
            wire.ProgressPercent?.Invoke(percent);
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
        SendCommand(MtkLegacyCommand.Shutdown);
        Ack();
        Write32(0);
        Ack();
    }
}
