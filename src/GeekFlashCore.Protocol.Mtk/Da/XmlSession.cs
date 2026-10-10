// SPDX-License-Identifier: AGPL-3.0-or-later
// XML wire sequence derived from penumbra (Shomy 2025-2026, AGPL-3.0-or-later).
using System.Text;
using System.Xml.Linq;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Internals;
using GeekFlashCore.Protocol.Mtk.Loaders;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal sealed partial class XmlSession(MtkWire wire, MtkProtocolOptions options) : IMtkDaSession
{
    private const string HostCapabilities =
        MtkXmlCommand.Prefix + MtkXmlCommand.DownloadFile + "^1@" +
        MtkXmlCommand.Prefix + MtkXmlCommand.FileSysOperation + "^1@" +
        MtkXmlCommand.Prefix + MtkXmlCommand.ProgressReport + "^1@" +
        MtkXmlCommand.Prefix + MtkXmlCommand.UploadFile + "^1@";

    public MtkDaKind Kind => MtkDaKind.Xml;

    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);
    public void Ack(string? value = null) => wire.SendFrame(Encoding.ASCII.GetBytes(value is null ? "OK\0" : "OK@" + value + "\0"));
    private string ReceiveText(int maximum = 65536) => new UTF8Encoding(false, true).GetString(wire.ReadSmallFrame(Math.Min(maximum, options.MaximumXmlSize))).TrimEnd('\0');
    private XElement ReceiveXml() => MtkXmlCodec.Parse(wire.ReadSmallFrame(options.MaximumXmlSize), options.MaximumXmlSize);
    private void Require(string command, XElement root)
    {
        // A DA may terminate a rejected command instead of requesting its file.
        // Consume that real END, not a guessed ACK sequence, before reporting failure.
        if (command != MtkXmlCommand.End && MtkXmlCodec.Value(root, "command") == MtkXmlCommand.Prefix + MtkXmlCommand.End)
        {
            CompleteEnd(root);
            throw wire.XmlFailure("UNEXPECTED_END", null, requiresReconnect: false);
        }
        bool matches = MtkXmlCodec.Value(root, "command") == MtkXmlCommand.Prefix + command;
        wire.Logger.Debug(Strings.XmlLifecycle, wire.Stage, wire.CommandName, command, matches);
        if (!matches)
            throw wire.Failure();
        var result = root.Descendants("result").ToArray();
        if (result.Length > 1 || command == MtkXmlCommand.End && result.Length != 1 || result.Length == 1 && result[0].HasElements)
            throw wire.Failure();
        if (result.Length == 1 && result[0].Value != "OK")
        {
            var messages = root.Descendants("message").ToArray();
            string? message = messages.Length == 1 && !messages[0].HasElements ? messages[0].Value : null;
            throw wire.XmlFailure(result[0].Value, message);
        }
    }

    private bool ReadAck(bool allowError = false)
    {
        string ack = ReceiveText(64);
        bool accepted = ack is "OK" or "OK@0x0";
        bool ignoredError = allowError && ack == "ERR";
        wire.TraceStatus(accepted ? 0u : 1u, accepted || ignoredError);
        if (!accepted && !ignoredError)
        {
            if (ack == "ERR" || ack.StartsWith("ERR!", StringComparison.Ordinal))
            {
                FinishRejectedCommand();
                throw wire.XmlFailure(ack, null, requiresReconnect: false);
            }
            throw wire.Failure();
        }
        return ignoredError;
    }

    private void CompleteEnd(XElement root)
    {
        bool matches = MtkXmlCodec.Value(root, "command") == MtkXmlCommand.Prefix + MtkXmlCommand.End;
        wire.Logger.Debug(Strings.XmlLifecycle, wire.Stage, wire.CommandName, MtkXmlCommand.End, matches);
        var results = root.Descendants("result").ToArray();
        var messages = root.Descendants("message").ToArray();
        if (!matches || results.Length != 1 || results[0].HasElements || results[0].Value.Length == 0 ||
            messages.Length > 1 || messages.Any(message => message.HasElements))
            throw wire.Failure();
        // A negative result still ends the command. ACK must succeed before the
        // failure becomes recoverable, and the operation must never report success.
        string result = results[0].Value;
        string? message = messages.Length == 1 ? messages[0].Value : null;
        Ack();
        wire.CompleteCommandBoundary();
        if (result != "OK")
            throw wire.XmlFailure(result, message, requiresReconnect: false);
    }

    private void FinishRejectedCommand()
    {
        // Reject/END recovery stays within the existing operation and event budgets.
        // Never consume START or send file bytes to make a failed write continue.
        for (int i = 0; i < options.MaximumMessages; i++)
        {
            var root = ReceiveXml();
            switch (MtkXmlCodec.Value(root, "command"))
            {
                case MtkXmlCommand.Prefix + MtkXmlCommand.ProgressReport:
                    Progress(root);
                    break;
                case MtkXmlCommand.Prefix + MtkXmlCommand.End:
                    try { CompleteEnd(root); }
                    catch (MtkProtocolException ex) when (!ex.RequiresReconnect) { /* Preserve the original rejection. */ }
                    return;
                default:
                    throw wire.Failure();
            }
        }
        throw wire.Failure();
    }

    public void Lifetime(string name)
    {
        var root = ReceiveXml();
        if (name == MtkXmlCommand.End)
        {
            CompleteEnd(root);
            return;
        }
        Require(name, root);
        Ack();
    }

    public void LifetimeIgnoringResult(string name)
    {
        var root = ReceiveXml();
        bool matches = MtkXmlCodec.Value(root, "command") == MtkXmlCommand.Prefix + name;
        wire.Logger.Debug(Strings.XmlLifecycle, wire.Stage, wire.CommandName, name, matches);
        var result = root.Descendants("result").ToArray();
        if (!matches || result.Length != 1 || result[0].HasElements || result[0].Value.Length == 0)
            throw wire.Failure();
        Ack();
    }

    public void Begin(string name, IReadOnlyDictionary<string, string> parameters)
    {
        byte[] data = MtkXmlCodec.Create(name, parameters); // Validate before consuming START.
        wire.TraceCommand(0, name); // Only the validated command name; no XML/parameter values.
        Lifetime(MtkXmlCommand.Start);
        wire.SendFrame(data);
        ReadAck();
    }

    private void Simple(string name, Dictionary<string, string> parameters)
    {
        Begin(name, parameters);
        Lifetime(MtkXmlCommand.End);
    }

    private bool BeginOptional(string name, IReadOnlyDictionary<string, string> parameters)
    {
        byte[] data = MtkXmlCodec.Create(name, parameters);
        wire.TraceCommand(0, name);
        Lifetime(MtkXmlCommand.Start);
        wire.SendFrame(data);
        string ack = ReceiveText(64);
        wire.TraceStatus(ack is "OK" or "OK@0x0" ? 0u : 1u, ack is "OK" or "OK@0x0");
        if (ack is "OK" or "OK@0x0")
            return true;
        if (ack != "ERR!UNSUPPORTED")
        {
            if (ack == "ERR" || ack.StartsWith("ERR!", StringComparison.Ordinal))
            {
                FinishRejectedCommand();
                throw wire.XmlFailure(ack, null, requiresReconnect: false);
            }
            throw wire.Failure();
        }
        // Unsupported is recoverable only after the complete command lifetime is confirmed.
        var end = ReceiveXml();
        if (MtkXmlCodec.Value(end, "command") != MtkXmlCommand.Prefix + MtkXmlCommand.End)
            throw wire.Failure();
        var results = end.Descendants("result").ToArray();
        if (results.Length != 1 || results[0].HasElements)
            throw wire.Failure();
        if (results[0].Value != "OK")
        {
            var messages = end.Descendants("message").ToArray();
            // Some v6 DAs put the machine status in result and a human description
            // in message. The description is not an authentication/status token.
            bool unsupportedResult = results[0].Value == "ERR!UNSUPPORTED" &&
                messages.Length <= 1 && messages.All(message => !message.HasElements);
            bool unsupportedMessage = results[0].Value == "ERR" && messages.Length == 1 &&
                !messages[0].HasElements && messages[0].Value == "ERR!UNSUPPORTED";
            if (!unsupportedResult && !unsupportedMessage)
            {
                CompleteEnd(end); // ACK a real END even when its result contradicts the rejection.
                throw wire.XmlFailure(results[0].Value, messages.Length == 1 && !messages[0].HasElements ? messages[0].Value : null,
                    requiresReconnect: false);
            }
        }

        Ack();
        wire.Check();
        wire.CompleteCommandBoundary();
        wire.Logger.ForContext("MtkSummary", true).ForContext("BootStage", wire.Stage).Warning(Strings.OptionalCommandUnsupported, name);
        return false;
    }

    private void AdvertiseHostCommands()
    {
        if (BeginOptional(MtkXmlCommand.HostSupportedCommands, Args(("host_capability", HostCapabilities))))
            Lifetime(MtkXmlCommand.End);
    }

    public void InitializeDa1()
    {
        wire.Stage = MtkBootStage.Da1;
        Simple(MtkXmlCommand.SetRuntimeParameter, Args(("checksum_level", "NONE"), ("battery_exist", "AUTO-DETECT"), ("da_log_level", "INFO"), ("log_channel", "UART"), ("system_os", OperatingSystem.IsWindows() ? "WINDOWS" : "LINUX"), ("initialize_dram", "YES")));
        AdvertiseHostCommands();
        Simple(MtkXmlCommand.SetHostInfo, Args(("info", "GeekFlashCore")));
        Begin(MtkXmlCommand.NotifyInitHw, Args());
        Progress();
        Lifetime(MtkXmlCommand.End);
    }

    public void Initialize(MtkDaImage image, MtkEmiImage? emi, MtkTargetInfo target, Func<MtkExploitStage, MtkDaImage> checkpoint)
    {
        image = checkpoint(MtkExploitStage.Da1Ready);
        var region = image.Entry.Regions[image.Entry.EntryRegionIndex + 1];
        long length = region.Length - region.SignatureLength;
        using Stream source = new MtkDataWindow(image.Source, region.FileOffset, length).OpenStream();
        string address = $"0x{region.Address:x}";
        Begin(MtkXmlCommand.BootTo, Args(("at_address", address), ("jmp_address", address), ("source_file", "MEM://0x0:0x0")));
        Download(length, source);
        Lifetime(MtkXmlCommand.End);
        wire.Stage = MtkBootStage.Da2;
        AdvertiseHostCommands();
        Begin(MtkXmlCommand.NotifyInitHw, Args());
        Progress();
        Lifetime(MtkXmlCommand.End);
        checkpoint(MtkExploitStage.Da2Ready);
    }

    public MtkDaAuthenticationState AuthenticationState { get; private set; }

    private bool BeginSlaProperty()
    {
        if (BeginOptional(MtkXmlCommand.GetSysProperty, Args(("key", "DA.SLA"), ("target_file", "MEM://0x0:0x200000"))))
            return true;
        AuthenticationState = MtkDaAuthenticationState.Unsupported;
        return false;
    }

    public byte[]? GetAuthenticationChallenge()
    {
        AuthenticationState = MtkDaAuthenticationState.NotQueried;
        if (!BeginSlaProperty())
            return null;
        using var property = new MemoryStream();
        Upload(property, null, options.MaximumXmlSize);
        Lifetime(MtkXmlCommand.End);
        var document = MtkXmlCodec.Parse(property.ToArray(), options.MaximumXmlSize, allowPropertyKey: true);
        var items = document.DescendantsAndSelf("item").Where(e => (string? )e.Attribute("key") == "DA.SLA").ToArray();
        if (items.Length != 1 || items[0].HasElements)
            throw new MtkResourceException("XML DA.SLA property");
        string state = items[0].Value.Trim();
        if (state == "DISABLED")
        {
            AuthenticationState = MtkDaAuthenticationState.NotRequired;
            return null;
        }

        if (state != "ENABLED")
            throw wire.Failure();
        Begin(MtkXmlCommand.SecurityGetDevFwInfo, Args(("target_file", "MEM://0x0:0x200000")));
        using var output = new MemoryStream();
        byte[]? challenge = null;
        try
        {
            Upload(output, null, options.MaximumXmlSize);
            Lifetime(MtkXmlCommand.End);
            challenge = output.ToArray();
            _ = MtkXmlCodec.Parse(challenge, options.MaximumXmlSize);
            return challenge;
        }
        catch
        {
            if (challenge is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(challenge);
            throw;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(output.GetBuffer());
        }
    }

    public void Authenticate(ReadOnlySpan<byte> response)
    {
        if (response.IsEmpty || response.Length > options.MaximumFrameSize)
            throw new MtkResourceException("DA SLA response length");
        Begin(MtkXmlCommand.SecuritySetFlashPolicy, Args(("source_file", "MEM://auth")));
        byte[] copy = response.ToArray();
        try
        {
            using var input = new MemoryStream(copy, false);
            Download(copy.Length, input);
            Lifetime(MtkXmlCommand.End);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(copy);
        }
    }

    public byte[] QueryFile(string command, int? maximum = null)
    {
        Begin(command, Args(("target_file", "MEM://0x0:0x200000")));
        using var output = new MemoryStream();
        try
        {
            Upload(output, null, Math.Min(maximum ?? options.MaximumXmlSize, options.MaximumXmlSize));
            Lifetime(MtkXmlCommand.End);
            return output.ToArray();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(output.GetBuffer());
        }
    }

    public byte[] GetSystemProperty(string key)
    {
        Begin(MtkXmlCommand.GetSysProperty, Args(("key", key), ("target_file", "MEM://0x0:0x200000")));
        using var output = new MemoryStream();
        try
        {
            Upload(output, null, options.MaximumXmlSize);
            Lifetime(MtkXmlCommand.End);
            return output.ToArray();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(output.GetBuffer());
        }
    }

    public uint ReadRegister(uint address)
    {
        Begin(MtkXmlCommand.ReadRegister, Args(("bit_width", "32"), ("base_address", $"0x{address:X}"), ("target_file", "MEM://0x0:0x4")));
        string size = ReceiveText(64);
        if (!size.StartsWith("OK@0x", StringComparison.Ordinal) || MtkXmlCodec.Number(size[3..]) != 4)
            throw wire.Failure();
        Ack();
        ReadAck();
        Ack();
        Span<byte> data = stackalloc byte[4];
        if (wire.ReadFrame(data) != 4)
            throw wire.Failure();
        Ack();
        Lifetime(MtkXmlCommand.End);
        return BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    public void WriteFile(string command, Stream source, long length)
    {
        Begin(command, Args(("source_file", "MEM://0x0:0x200000")));
        Download(length, source);
        Lifetime(MtkXmlCommand.End);
    }

    public void WriteRegister(uint address, uint value)
    {
        Begin(MtkXmlCommand.WriteRegister, Args(("bit_width", "32"), ("base_address", $"0x{address:X}"), ("source_file", "MEM://0x0:0x4")));
        using var source = new MemoryStream(MtkWire.Le32(value), false);
        Download(4, source);
        Lifetime(MtkXmlCommand.End);
    }

    public MtkStorageInfo GetStorage()
    {
        Begin(MtkXmlCommand.GetHwInfo, Args(("target_file", "MEM://0x0:0x200000")));
        using var output = new MemoryStream();
        Upload(output, null, options.MaximumXmlSize);
        Lifetime(MtkXmlCommand.End);
        XElement root = MtkXmlCodec.Parse(output.ToArray(), options.MaximumXmlSize);
        string kind = MtkXmlCodec.Value(root, "storage");
        var sections = root.DescendantsAndSelf(kind.ToLowerInvariant()).ToArray();
        if (sections.Length != 1)
            throw new MtkResourceException("XML storage");
        XElement section = sections[0];
        ulong Number(string key) => MtkXmlCodec.Number(MtkXmlCodec.Value(section, key));
        int block = checked((int)Number("block_size"));
        List<MtkStorageRegion> regions = [];
        if (kind == "NAND")
        {
            ulong page = Number("page_size"), spare = Number("spare_size"), total = Number("total_size");
            if (page is < 512 or > 65536 || (page & (page - 1)) != 0 || spare > page || block < (long)page || block > 16777216 || block % (long)page != 0 || total == 0 || total > long.MaxValue || total % (ulong)block != 0)
                throw new MtkResourceException("XML NAND geometry");
            ulong usable = options.NandLogicalCapacity is { } configured ? (ulong)configured : total;
            if (usable == 0 || usable > total || usable % (ulong)block != 0)
                throw new MtkResourceException("NAND logical capacity");
            var region = new MtkStorageRegion(MtkStorageKind.Nand, 8, "NAND-WHOLE", usable, (int)page)
            {
                CanWrite = options.EnableNandLogicalWrites && options.NandLogicalCapacity.HasValue,
                EraseBlockSize = block
            };
            return new(MtkStorageKind.Nand, Array.AsReadOnly(new[] { region }), 8, 0)
            {
                Nand = new(0, (int)page, (int)spare, block, total, usable, false)
                {
                    LogicalCapacityConfirmed = options.NandLogicalCapacity.HasValue
                }
            };
        }

        if (kind == "EMMC")
        {
            string[] sizes = ["boot1_size", "boot2_size", "rpmb_size", "gp1_size", "gp2_size", "gp3_size", "gp4_size", "user_size"];
            ulong rpmb = 0;
            for (uint id = 1; id <= 8; id++)
            {
                ulong size = Number(sizes[id - 1]);
                if (id == 3)
                {
                    rpmb = size;
                    continue;
                }

                if (size > 0)
                    regions.Add(new(MtkStorageKind.Emmc, id, id switch
                    {
                        1 => "EMMC-BOOT1",
                        2 => "EMMC-BOOT2",
                        8 => "EMMC-USER",
                        _ => $"EMMC-GP{id - 3}"}, size, block));
            }

            if (!regions.Any(r => r.WireId == 8) || rpmb % 256 != 0)
                throw new MtkResourceException("XML eMMC geometry");
            return new(MtkStorageKind.Emmc, regions.AsReadOnly(), 8, checked((uint)(rpmb / 256)));
        }

        if (kind != "UFS")
            throw new MtkCapabilityException("storage kind");
        // Wire XML uses lua*; retain the accepted lu* layout without combining schemas.
        bool usesLuaSizes = section.DescendantsAndSelf().Any(e => e.Name == "lua0_size" || e.Name == "lua1_size" || e.Name == "lua2_size");
        if (usesLuaSizes && section.DescendantsAndSelf().Any(e => e.Name == "lu0_size" || e.Name == "lu1_size" || e.Name == "lu2_size"))
            throw new MtkResourceException("XML UFS geometry");
        string sizePrefix = usesLuaSizes ? "lua" : "lu";
        for (uint i = 0; i < 3; i++)
        {
            ulong size = Number($"{sizePrefix}{i}_size");
            if (size > 0)
                regions.Add(new(MtkStorageKind.Ufs, i + 1, $"UFS-LUA{i}", size, block));
        }

        if (!regions.Any(r => r.WireId == 3))
            throw new MtkResourceException("XML UFS geometry");
        return new(MtkStorageKind.Ufs, regions.AsReadOnly(), 3, 0);
    }

    public void Read(MtkStorageRegion region, long offset, long length, Stream output)
    {
        wire.TraceStorage(MtkTransferKind.Read, region, offset, length);
        Begin(MtkXmlCommand.ReadFlash, Args(("partition", region.Name), ("target_file", region.Name), ("length", $"0x{length:X}"), ("offset", $"0x{offset:X}")));
        Upload(output, length, length);
        Lifetime(MtkXmlCommand.End);
    }

    public void Write(MtkStorageRegion region, long offset, long length, Stream input)
    {
        if (!region.CanWrite)
            throw new MtkCapabilityException("read-only storage region");
        wire.TraceStorage(MtkTransferKind.Write, region, offset, length);
        Begin(MtkXmlCommand.WriteFlash, Args(("partition", region.Name), ("source_file", $"MEM:\\0x0:0x{length:X}"), ("offset", $"0x{offset:X}")));
        FileSize(length);
        Progress();
        Download(length, input);
        Lifetime(MtkXmlCommand.End);
    }

    public void Erase(MtkStorageRegion region, long offset, long length)
    {
        if (!region.CanWrite || region.EraseBlockSize == 0)
            throw new MtkCapabilityException("erase geometry/policy");
        if (offset % region.EraseBlockSize != 0 || length % region.EraseBlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        wire.TraceStorage(MtkTransferKind.Erase, region, offset, length);
        Begin(MtkXmlCommand.EraseFlash, Args(("partition", region.Name), ("length", $"0x{length:X}"), ("offset", $"0x{offset:X}")));
        Progress();
        Lifetime(MtkXmlCommand.End);
    }

    public void Reboot(ProtocolRebootMode mode)
    {
        if (mode == ProtocolRebootMode.PowerOff)
            throw new MtkCapabilityException("XML power off");
        if (mode == ProtocolRebootMode.Download)
            Simple(MtkXmlCommand.SetBootMode, Args(("mode", "FASTBOOT"), ("connect_type", "USB"), ("mobile_log", "OFF"), ("adb", "OFF")));
        Simple(MtkXmlCommand.Reboot, Args(("action", "IMMEDIATE")));
    }

    public long Upload(Stream output, long? expected, long maximum, XElement? request = null, Action? beforeFinalAck = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException(nameof(output));
        if (maximum <= 0 || expected is <= 0 || expected > maximum)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        request ??= ReceiveXml();
        Require(MtkXmlCommand.UploadFile, request);
        int packet = PacketSize(request);
        Ack();
        string sizeText = ReceiveText(64);
        if (!sizeText.StartsWith("OK@0x", StringComparison.Ordinal))
            throw wire.Failure();
        ulong value = MtkXmlCodec.Number(sizeText[3..]);
        if (value == 0 || value > (ulong)maximum || expected is { } exact && value != (ulong)exact)
            throw wire.Failure();
        long size = checked((long)value);
        Ack();
        int window = (int)Math.Min(size, Math.Min(packet, options.MaximumXmlReadPacketLength));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(window);
        try
        {
            for (long done = 0; done < size;)
            {
                ReadAck();
                Ack();
                int want = (int)Math.Min(packet, size - done);
                int n = wire.ReadXmlStreamFrame(output, want, buffer.AsSpan(0, window));
                if (done + n == size)
                    beforeFinalAck?.Invoke();
                Ack();
                done += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, true);
        }

        return size;
    }

    public void Download(long length, Stream input, XElement? request = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
            throw new ArgumentException(nameof(input));
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        request ??= ReceiveXml();
        Require(MtkXmlCommand.DownloadFile, request);
        int packet = PacketSize(request);
        Ack();
        Ack(length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ReadAck();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(packet);
        try
        {
            for (long done = 0; done < length;)
            {
                wire.Check();
                int n = (int)Math.Min(packet, length - done);
                input.ReadExactly(buffer.AsSpan(0, n));
                Ack("0");
                ReadAck();
                // Only this file packet uses its negotiated native write limit. Keep the
                // single FLOW/ACK boundary and do not leak it into control/extension frames.
                wire.SendFrame(buffer.AsSpan(0, n), Math.Min(packet, options.MaximumXmlWritePacketLength));
                ReadAck();
                done += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, true);
        }
    }

    public void DownloadOverflow(Stream input, long advertisedLength, ReadOnlySpan<byte> trailer)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (advertisedLength <= 0 || advertisedLength > options.MaximumFrameSize || trailer.IsEmpty)
            throw new MtkResourceException("XML overflow length");
        if (!input.CanSeek || input.Length - input.Position <= 0 || input.Length - input.Position > options.MaximumFrameSize)
            throw new MtkResourceException("XML overflow source");
        int sourceLength = checked((int)(input.Length - input.Position));
        int actualLength = checked(sourceLength + trailer.Length);
        if (actualLength > options.MaximumFrameSize || advertisedLength >= actualLength)
            throw new MtkResourceException("XML overflow length");
        XElement request = ReceiveXml();
        Require(MtkXmlCommand.DownloadFile, request);
        _ = PacketSize(request);
        Ack();
        Ack(advertisedLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ReadAck();
        byte[] payload = ArrayPool<byte>.Shared.Rent(actualLength);
        try
        {
            input.ReadExactly(payload.AsSpan(0, sourceLength));
            trailer.CopyTo(payload.AsSpan(sourceLength));
            Ack("0");
            ReadAck();
            wire.SendFrame(payload.AsSpan(0, actualLength));
            ReadAck();
            wire.SendFrame("GETBAITED\0"u8);
            ReadAck(allowError: true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload.AsSpan(0, actualLength));
            ArrayPool<byte>.Shared.Return(payload, true);
        }
        LifetimeIgnoringResult(MtkXmlCommand.End);
    }

    private int PacketSize(XElement request)
    {
        ulong size = MtkXmlCodec.Number(MtkXmlCodec.Value(request, "packet_length"));
        if (size is 0 or > MtkProtocolOptions.MaximumXmlPacketLength)
            throw wire.Failure();
        wire.Logger.Debug(Strings.XmlPacketLength, wire.Stage, wire.CommandName, size);
        return (int)size; // Must retain the negotiated XML packet boundary.
    }

    private void FileSize(long length)
    {
        var request = ReceiveXml();
        Require(MtkXmlCommand.FileSysOperation, request);
        if (MtkXmlCodec.Value(request, "key") != "FILE-SIZE")
            throw wire.Failure();
        if (MtkXmlCodec.Value(request, "file_path") != $"MEM:\\0x0:0x{length:X}")
            throw new MtkResourceException("XML virtual file");
        Ack();
        Ack("0x" + length.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Progress(XElement? request = null)
    {
        Require(MtkXmlCommand.ProgressReport, request ?? ReceiveXml());
        Ack();
        for (int i = 0; i < options.MaximumProgressEvents; i++)
        {
            string text = ReceiveText(128);
            if (text == "OK!EOT")
            {
                Ack();
                return;
            }

            // DAs may terminate the percentage field with '@' (captured: 100@).
            // Consume only that optional delimiter, never arbitrary fields or whitespace.
            const string prefix = "OK!PROGRESS@";
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
                throw wire.XmlFailure("INVALID_PROGRESS", null);
            ReadOnlySpan<char> value = text.AsSpan(prefix.Length);
            if (value.EndsWith("@", StringComparison.Ordinal))
                value = value[..^1];
            if (!uint.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out uint percent) || percent > 100)
                throw wire.XmlFailure("INVALID_PROGRESS", null);
            Ack();
            wire.Logger.Debug(Strings.WireProgress, wire.Stage, wire.Command, percent);
            wire.ProgressPercent?.Invoke((int)percent);
        }

        throw wire.Failure();
    }

    public long ReadNamed(string name, Stream destination, long maximum)
    {
        Begin(MtkXmlCommand.ReadPartition, Args(("partition", name), ("target_file", name + ".bin")));
        long length = Upload(destination, null, maximum);
        Lifetime(MtkXmlCommand.End);
        return length;
    }

    public void WriteNamed(string name, Stream source, long length)
    {
        Begin(MtkXmlCommand.WritePartition, Args(("partition", name), ("source_file", name + ".bin")));
        bool downloaded = false;
        for (int i = 0; i < options.MaximumMessages; i++)
        {
            XElement request = ReceiveXml();
            string command = MtkXmlCodec.Value(request, "command");
            switch (command)
            {
                case MtkXmlCommand.Prefix + MtkXmlCommand.ProgressReport:
                    Progress(request);
                    break;
                case MtkXmlCommand.Prefix + MtkXmlCommand.FileSysOperation:
                    Require(MtkXmlCommand.FileSysOperation, request);
                    if (MtkXmlCodec.Value(request, "key") != "FILE-SIZE" || MtkXmlCodec.Value(request, "file_path") != name + ".bin")
                        throw new MtkResourceException("named virtual file");
                    Ack();
                    Ack("0x" + length.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case MtkXmlCommand.Prefix + MtkXmlCommand.DownloadFile:
                    if (downloaded)
                        throw wire.Failure();
                    Download(length, source, request);
                    downloaded = true;
                    break;
                case MtkXmlCommand.Prefix + MtkXmlCommand.End:
                    CompleteEnd(request);
                    if (!downloaded)
                        throw wire.Failure();
                    return;
                default:
                    throw wire.Failure();
            }
        }

        throw wire.Failure();
    }

    public void EraseNamed(string name)
    {
        Begin(MtkXmlCommand.ErasePartition, Args(("partition", name)));
        Progress();
        Lifetime(MtkXmlCommand.End);
    }
}
