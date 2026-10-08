// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard wire commands: B. Kerler, mtkclient/Library/mtk_preloader.py, 2018-2024, GPLv3.
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Internals;
using GeekFlashCore.Protocol.Mtk.Loaders;
using Serilog;

namespace GeekFlashCore.Protocol.Mtk.Brom;

internal sealed partial class MtkBromSession(MtkWire wire, MtkProtocolOptions options)
{
    private bool _watchdogDisabled;
    public MtkTargetInfo Probe(bool initializeWatchdog = false, Action? identified = null)
    {
        _watchdogDisabled = false;
        ReadOnlySpan<byte> handshake = [0xa0, 0x0a, 0x50, 0x05];
        for (int i = 0; i < handshake.Length; i++)
        {
            Log.ForContext<MtkBromSession>().Debug(Strings.HandshakeStep, i + 1, handshake.Length);
            wire.WriteByte(handshake[i]);
            byte response = wire.ReadByte();
            if (i == 0 && response == 0xa0)
                break; // Candidate only, FD/FC must still prove identity.
            int prefixes = 0;
            while (i == 0 && response != 0x5f && prefixes++ < options.MaximumHandshakePrefix)
                response = wire.ReadByte();
            if (response != (byte)~handshake[i])
                throw wire.Failure();
        }
        var hardware = GetHardwareCode();
        if (hardware.Code == 0)
            throw wire.Failure();
        // Recovery must stop before the first chip-specific write, even if later Probe queries fail.
        identified?.Invoke();
        var chip = MtkChipCatalog.Find(hardware.Code);
        var watchdog = (initializeWatchdog || options.InitializeWatchdogOnProbe)
            ? DisableWatchdog(new(hardware.Code, 0, 0, 0, 0, 0, MtkBootStage.Unknown, new(0)))
            : MtkWatchdogState.NotRequested;
        var security = GetTargetConfiguration();
        byte bl = GetBootLoaderVersion();
        wire.Stage = bl == 0xfe ? MtkBootStage.Brom : MtkBootStage.Preloader;
        byte brom = GetBromVersion();
        var versions = GetHardwareSoftwareVersion();
        return new(hardware.Code, versions.SubCode, versions.HardwareVersion, versions.SoftwareVersion,
            brom, bl, wire.Stage, security)
        {
            InitialHardwareVersion = hardware.Version,
            ChipName = chip?.Name,
            ChipDescription = chip?.Description,
            DaHardwareCode = options.DaHardwareCode ?? chip?.DaHardwareCode ?? hardware.Code,
            WatchdogState = watchdog
        };
    }
    public void Upload(MtkDaImage image, Func<MtkAuthenticationKind, ReadOnlyMemory<byte>, MtkSensitiveBuffer>? signer = null)
    {
        MtkDaRegion region = image.Entry.Regions[image.Entry.EntryRegionIndex];
        SendDownloadAgent(region.Address, region.Length, region.SignatureLength,
            new MtkDataWindow(image.Source, region.FileOffset, region.Length), signer);
        if(options.LegacyIoT)
        {
            var second=image.Entry.Regions[image.Entry.EntryRegionIndex+1];
            SendDownloadAgent(second.Address,second.Length,second.SignatureLength,new MtkDataWindow(image.Source,second.FileOffset,second.Length),signer);
        }
        JumpDownloadAgent(region.Address);
    }
    public void SendResource(MtkBromCommand command, ReadOnlySpan<byte> data)
    {
        int paddedLength = checked(data.Length + (data.Length & 1));
        if (command is not (MtkBromCommand.SendCertificate or MtkBromCommand.SendAuthentication) ||
            data.IsEmpty || paddedLength > options.MaximumFrameSize)
            throw new MtkResourceException("authentication");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(paddedLength);
        try
        {
            data.CopyTo(buffer);
            if (paddedLength != data.Length)
                buffer[data.Length] = 0;
            Command(command);
            wire.Echo32((uint)paddedLength);
            ushort status = wire.Read16();
            if (command == MtkBromCommand.SendAuthentication && status == 0x1d0c)
                return;
            CheckStatusValue(status);
            using var source = new MemoryStream(buffer, 0, paddedLength, false);
            ushort checksum = UploadBytes(source, paddedLength);
            ushort actual = wire.Read16();
            CheckStatus();
            if (actual != checksum)
                throw wire.Failure(actual);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    public byte[]? StartSla()
    {
        Command(MtkBromCommand.SerialLinkAuthentication);
        ushort status = wire.Read16();
        if (status == 0x7017)
            return null;
        CheckStatusValue(status);
        uint size = wire.Read32();
        if (size is < 16 or > 4096)
            throw wire.Failure();
        byte[] challenge = new byte[(int)size];
        try
        {
            wire.Read(challenge);
            return challenge;
        }
        catch
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(challenge);
            throw;
        }
    }
    public void FinishSla(ReadOnlySpan<byte> response)
    {
        if (response.Length is < 16 or > 4096)
            throw new MtkResourceException("BROM SLA response");
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)response.Length);
        wire.Write(length);
        if (wire.Read32() != response.Length)
            throw wire.Failure();
        CheckStatus();
        wire.Write(response);
        uint status = wire.Read32();
        if (status > 0xff)
            throw wire.Failure(status);
    }
    public MtkWatchdogState DisableWatchdog(MtkTargetInfo target)
    {
        if (_watchdogDisabled)
            return MtkWatchdogState.Disabled;
        if ((options.ChipProfile ?? MtkChipCatalog.Find(target.HardwareCode)?.Watchdog) is not { } profile)
            return MtkWatchdogState.ProfileUnavailable;
        if (profile.HardwareCode != target.HardwareCode || profile.WatchdogAddress == 0 ||
            profile.WatchdogWidth is not (16 or 32) || profile.WatchdogAddress % (profile.WatchdogWidth / 8) != 0 ||
            profile.WatchdogWidth == 16 && profile.WatchdogValue > ushort.MaxValue)
            throw new MtkResourceException("chip profile");
        if (profile.WatchdogWidth == 16)
            Write16(profile.WatchdogAddress, [(ushort)profile.WatchdogValue]);
        else
            Write32(profile.WatchdogAddress, [profile.WatchdogValue]);
        _watchdogDisabled = true;
        return MtkWatchdogState.Disabled;
    }
    private ushort UploadBytes(Stream source, long length)
    {
        const int uploadPacketSize = 64;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(uploadPacketSize);
        ushort checksum = 0;
        bool low = true;
        byte first = 0;
        try
        {
            while (length > 0)
            {
                wire.Check();
                int n = (int)Math.Min(uploadPacketSize, length);
                source.ReadExactly(buffer.AsSpan(0, n));
                foreach (byte b in buffer.AsSpan(0, n))
                {
                    if (low)
                        first = b;
                    else
                        checksum ^= (ushort)(first | b << 8);
                    low = !low;
                }
                wire.Write(buffer.AsSpan(0, n));
                length -= n;
            }
            if (!low)
            {
                checksum ^= first;
                wire.WriteByte(0);
            }
            wire.ZeroLengthPacket();
            return checksum;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
}
