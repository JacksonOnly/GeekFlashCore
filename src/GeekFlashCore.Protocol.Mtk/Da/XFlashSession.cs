// SPDX-License-Identifier: AGPL-3.0-or-later
// Wire order derived from penumbra (Shomy 2025-2026, AGPL-3.0-or-later)
// and mtkclient (B. Kerler 2018-2024, GPLv3); bounded synchronous rewrite.
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Internals;
using GeekFlashCore.Protocol.Mtk.Loaders;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal sealed class XFlashSession(MtkWire wire, MtkProtocolOptions options) : IMtkDaSession
{
    private const uint ProgressTick = 0x40040004;
    private const uint ProgressComplete = 0x40040005;
    private const int EfuseParameterSize = 0xf8;
    private const int MaximumEfuseSize = 0x5000;
    private const int InitialWritePacketLength = 0x8000;

    public MtkDaKind Kind => MtkDaKind.XFlash;
    public void Command(uint command)
    {
        wire.TraceCommand(command, Enum.IsDefined((MtkXFlashCommand)command) ? ((MtkXFlashCommand)command).ToString() : "Custom");
        wire.SendUInt32Frame(command);
        wire.ReadStatus();
    }
    private void Command(MtkXFlashCommand command) => Command((uint)command);
    public void Parameters(params byte[][] data)
    {
        foreach (var part in data)
            wire.SendFrame(part);
        wire.ReadStatus();
    }
    public byte[] Control(MtkXFlashCommand command, int maximum = 512)
    {
        Command(MtkXFlashCommand.DeviceCtrl);
        Command(command);
        byte[] result = wire.ReadSmallFrame(Math.Min(maximum, options.MaximumFrameSize));
        try
        {
            wire.ReadStatus();
            return result;
        }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(result); throw; }
    }
    public void Control(MtkXFlashCommand command, params byte[][] data)
    {
        Command(MtkXFlashCommand.DeviceCtrl);
        Command(command);
        Parameters(data);
    }
    public void Initialize(MtkDaImage image, MtkEmiImage? emi, MtkTargetInfo target,
        Func<MtkExploitStage, MtkDaImage> checkpoint)
    {
        // Pre-negotiation XFlash FLOW payload limit; BROM and other DA dialects remain independent.
        wire.WritePacketLength = Math.Min(options.BufferSize, InitialWritePacketLength);
        if (wire.ReadByte() != 0xc0)
            throw wire.Failure();
        wire.Stage = MtkBootStage.Da1;
        wire.TraceCommand((uint)MtkXFlashCommand.SyncSignal, nameof(MtkXFlashCommand.SyncSignal));
        wire.SendUInt32Frame((uint)MtkXFlashCommand.SyncSignal);
        byte[] environment = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(environment, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(environment.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(environment.AsSpan(8), OperatingSystem.IsWindows() ? 0u : 1u);
        wire.TraceCommand((uint)MtkXFlashCommand.SetupEnvironment, nameof(MtkXFlashCommand.SetupEnvironment));
        Parameters(MtkWire.Le32((uint)MtkXFlashCommand.SetupEnvironment), environment);
        wire.TraceCommand((uint)MtkXFlashCommand.SetupHwInitParams, nameof(MtkXFlashCommand.SetupHwInitParams));
        Parameters(MtkWire.Le32((uint)MtkXFlashCommand.SetupHwInitParams), new byte[4]);
        wire.ReadStatus((uint)MtkXFlashCommand.SyncSignal);
        byte[] agent = Control(MtkXFlashCommand.GetConnectionAgent);
        if (!agent.AsSpan().SequenceEqual("preloader"u8) && !agent.AsSpan().SequenceEqual("brom"u8))
            throw new MtkResourceException("connection agent");
        if (agent.AsSpan().SequenceEqual("brom"u8))
        {
            if (emi is null)
                throw new MtkResourceException("EMI");
            var emiSource = emi.BloaderInfoSource ?? emi.Source;
            if (emiSource.Length <= 0 || emiSource.Length > options.MaximumFrameSize)
                throw new MtkResourceException("EMI size");
            using Stream emiStream = emiSource.OpenStream();
            wire.Logger.Debug(Strings.EmiWindowSelected, emi.BloaderInfoSource is null ? "HostSource" : "BloaderInfo", emi.Version, emiSource.Length);
            MtkDiagnostics.Summary(wire.Logger, Strings.EmiStarted, Kind, emiSource.Length);
            Command(MtkXFlashCommand.InitExtRam);
            wire.SendUInt32Frame((uint)emiSource.Length);
            SendStreamFrame(emiStream, emiSource.Length);
            wire.ReadStatus(); // The parameter group, not each frame, is acknowledged.
            MtkDiagnostics.Summary(wire.Logger, Strings.EmiCompleted, Kind);
        }
        else MtkDiagnostics.Summary(wire.Logger, Strings.EmiNotRequired, Kind);
        Control(MtkXFlashCommand.SetChecksumLevel, new byte[4]);
        QueryPacketLength();
        image = checkpoint(MtkExploitStage.Da1Ready);
        var region = image.Entry.Regions[image.Entry.EntryRegionIndex + 1];
        long length = region.Length - region.SignatureLength;
        using Stream source = new MtkDataWindow(image.Source, region.FileOffset, length).OpenStream();
        MtkDiagnostics.Summary(wire.Logger, Strings.Da2BootRequested, region.Address, length);
        Command(MtkXFlashCommand.BootTo);
        wire.Logger.Debug(Strings.Da2BootCommandAccepted);
        byte[] range = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(range, region.Address);
        BinaryPrimitives.WriteUInt64LittleEndian(range.AsSpan(8), (ulong)length);
        wire.SendFrame(range);
        SendStreamFrame(source, length);
        wire.Logger.Debug(Strings.Da2PayloadSent, length);
        wire.ReadStatus();
        wire.ReadStatus(0, (uint)MtkXFlashCommand.SyncSignal);
        MtkDiagnostics.Summary(wire.Logger, Strings.Da2BootConfirmed);
        wire.Stage = MtkBootStage.Da2;
        checkpoint(MtkExploitStage.Da2Ready);
    }
    public void CompleteAuthentication() => QueryPacketLength();
    public MtkDaAuthenticationState AuthenticationState { get; private set; }
    public byte[]? GetAuthenticationChallenge()
    {
        AuthenticationState = MtkDaAuthenticationState.NotQueried;
        Command(MtkXFlashCommand.DeviceCtrl);
        wire.TraceCommand((uint)MtkXFlashCommand.SlaEnabledStatus, nameof(MtkXFlashCommand.SlaEnabledStatus));
        wire.SendUInt32Frame((uint)MtkXFlashCommand.SlaEnabledStatus);
        uint status = wire.ReadStatus((uint)MtkXFlashStatus.Success, (uint)MtkXFlashStatus.UnsupportedCtrlCode);
        if (status == (uint)MtkXFlashStatus.UnsupportedCtrlCode)
        {
            // A complete subcommand rejection has no result or trailing ACK. Do not consume
            // the next command's response. Parent, data, final status and I/O errors stay fatal.
            AuthenticationState = MtkDaAuthenticationState.Unsupported;
            wire.Logger.ForContext("MtkSummary", true).Warning(Strings.SlaStatusUnsupported,
                (uint)MtkXFlashCommand.SlaEnabledStatus, status);
            return null;
        }
        Span<byte> state = stackalloc byte[4];
        if (wire.ReadFrame(state) != state.Length)
            throw wire.Failure();
        wire.ReadStatus();
        uint enabled = BinaryPrimitives.ReadUInt32LittleEndian(state);
        if (enabled > 1)
            throw wire.Failure();
        if (enabled == 0)
        {
            AuthenticationState = MtkDaAuthenticationState.NotRequired;
            return null;
        }
        byte[] challenge = Control(MtkXFlashCommand.GetDevFwInfo, maximum: options.MaximumFrameSize);
        if (challenge.Length < 20)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(challenge);
            throw wire.Failure();
        }
        return challenge;
    }
    public void Authenticate(ReadOnlySpan<byte> response)
    {
        if (response.IsEmpty || response.Length > options.MaximumFrameSize)
            throw new MtkResourceException("DA SLA response length");
        byte[] copy = response.ToArray();
        try
        {
            Control(MtkXFlashCommand.SetRemoteSecPolicy, copy);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(copy); }
    }
    private void QueryPacketLength()
    {
        byte[] packet = Control(MtkXFlashCommand.GetPacketLength);
        wire.Logger.Debug(Strings.PacketLengthResponse, wire.Stage, packet.Length);
        if (packet.Length != 8)
            throw wire.Failure();
        uint write = BinaryPrimitives.ReadUInt32LittleEndian(packet), read = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
        bool valid = write is >= 512 and <= MtkProtocolOptions.MaximumXFlashPacketLength &&
            read is >= 512 and <= MtkProtocolOptions.MaximumXFlashPacketLength;
        wire.Logger.Debug(Strings.PacketLengthsOffered, wire.Stage, write, read, valid);
        if (!valid)
            throw wire.Failure();
        wire.WritePacketLength = (int)Math.Min(write, (uint)options.BufferSize);
        wire.ReadPacketLength = (int)Math.Min(read, (uint)options.MaximumXFlashDataFrameSize);
        wire.Logger.Debug(Strings.PacketLengths, wire.WritePacketLength, wire.ReadPacketLength, write, read);
    }
    public MtkStorageInfo GetStorage()
    {
        byte[] emmc = Control(MtkXFlashCommand.GetEmmcInfo);
        if (emmc.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(emmc) is 1 or 2)
            return MtkStorageDecoder.Emmc(emmc);
        if (emmc.Any(b => b != 0))
            throw new MtkResourceException("eMMC info");
        byte[] ufs = Control(MtkXFlashCommand.GetUfsInfo);
        if (ufs.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(ufs) == 0x30)
            return MtkStorageDecoder.Ufs(ufs);
        if (ufs.Any(b => b != 0)) throw new MtkResourceException("UFS info");
        byte[] nand = Control(MtkXFlashCommand.GetNandInfo);
        if (nand.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(nand) != 0)
            return MtkStorageDecoder.Nand(nand, options.EnableNandLogicalWrites);
        if (nand.Any(b => b != 0)) throw new MtkResourceException("NAND info");
        return MtkStorageDecoder.Nor(Control(MtkXFlashCommand.GetNorInfo), options.NorEraseBlockSize);
    }
    public void Read(MtkStorageRegion region, long offset, long length, Stream output)
    {
        wire.TraceStorage(MtkTransferKind.Read, region, offset, length);
        Command(MtkXFlashCommand.ReadData);
        Parameters(FlashParams(region, offset, length));
        wire.ReadStatus();
        ReceiveStream(output, length);
    }
    private void ReceiveStream(Stream output, long length)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(options.BufferSize);
        try
        {
            long done = 0;
            while (done < length)
            {
                int maximum = (int)Math.Min(wire.ReadPacketLength, length - done);
                int n = wire.ReadStreamFrame(output, maximum, buffer.AsSpan(0, options.BufferSize));
                done += n;
                wire.SendUInt32Frame(0);
                wire.ReadStatus();
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    public void Write(MtkStorageRegion region, long offset, long length, Stream input)
    {
        if (!region.CanWrite) throw new MtkCapabilityException("NAND logical writes");
        wire.TraceStorage(MtkTransferKind.Write, region, offset, length);
        Command(MtkXFlashCommand.WriteData);
        Parameters(FlashParams(region, offset, length));
        SendStream(input, length);
    }
    private void SendStream(Stream input, long length)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(wire.WritePacketLength);
        try
        {
            for (long done = 0; done < length;)
            {
                wire.Check();
                int n = (int)Math.Min(wire.WritePacketLength, length - done);
                input.ReadExactly(buffer.AsSpan(0, n));
                wire.SendUInt32Frame(0);
                wire.SendUInt32Frame(MtkWire.Sum(buffer.AsSpan(0, n)));
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
        wire.TraceStorage(MtkTransferKind.Erase, region, offset, length);
        DownloadInfo(MtkXFlashCommand.StartDlInfo);
        Command(MtkXFlashCommand.Format);
        Parameters(FlashParams(region, offset, length, erase: true));
        ReadProgress();
        DownloadInfo(MtkXFlashCommand.EndDlInfo);
    }
    public void Reboot(ProtocolRebootMode mode)
    {
        Command(MtkXFlashCommand.Shutdown);
        byte[] p = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(p, mode == ProtocolRebootMode.PowerOff ? 0u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), mode == ProtocolRebootMode.Download ? 2u : 0u);
        Parameters(p);
    }
    public byte[] ReadEfuses()
    {
        Command(MtkXFlashCommand.ReadEfuse);
        Parameters(new byte[EfuseParameterSize]);
        byte[] result = wire.ReadSmallFrame(Math.Min(options.MaximumFrameSize, MaximumEfuseSize));
        try
        {
            wire.SendUInt32Frame(0);
            wire.ReadStatus();
            return result;
        }
        catch
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }
    public void WriteEfuses(ReadOnlySpan<byte> data)
    {
        Command(MtkXFlashCommand.WriteEfuse);
        wire.SendFrame(data);
        wire.SendFrame(new byte[EfuseParameterSize]);
        wire.ReadStatus();
        wire.ReadStatus();
    }
    private void DownloadInfo(MtkXFlashCommand action)
    {
        Command(MtkXFlashCommand.DeviceCtrl);
        Command(action);
        wire.ReadStatus();
    }
    public long ReadNamed(string name, Stream destination, long maximum)
    {
        Command(MtkXFlashCommand.Upload);
        Parameters(System.Text.Encoding.ASCII.GetBytes(name));
        byte[] bytes = wire.ReadSmallFrame(8);
        wire.ReadStatus();
        if (bytes.Length != 8)
            throw new MtkResourceException("partition upload size");
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if (size == 0 || size > (ulong)maximum)
            throw new MtkResourceException("partition upload limit");
        ReceiveStream(destination, (long)size);
        return (long)size;
    }
    public void WriteNamed(string name, Stream source, long length)
    {
        DownloadInfo(MtkXFlashCommand.StartDlInfo);
        Command(MtkXFlashCommand.Download);
        byte[] size = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)length);
        Parameters(System.Text.Encoding.ASCII.GetBytes(name), size);
        SendStream(source, length);
        DownloadInfo(MtkXFlashCommand.EndDlInfo);
    }
    public void EraseNamed(string name)
    {
        DownloadInfo(MtkXFlashCommand.StartDlInfo);
        Command(MtkXFlashCommand.FormatPartition);
        // FORMAT_PARTITION sends progress immediately after the name, without a parameter ACK.
        wire.SendFrame(System.Text.Encoding.ASCII.GetBytes(name));
        ReadProgress();
        DownloadInfo(MtkXFlashCommand.EndDlInfo);
    }
    private void ReadProgress()
    {
        Span<byte> percent = stackalloc byte[4];
        for (int i = 0; i < options.MaximumProgressEvents; i++)
        {
            uint status = wire.ReadStatus(ProgressTick, ProgressComplete);
            if (status == ProgressComplete)
                return;
            if (wire.ReadFrame(percent) != percent.Length)
                throw wire.Failure();
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(percent);
            if (value > 100)
                throw wire.Failure();
            wire.SendUInt32Frame(0);
            wire.Logger.Debug(Strings.WireProgress, wire.Stage, wire.Command, value);
            wire.ProgressPercent?.Invoke((int)value);
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
