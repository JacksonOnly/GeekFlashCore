// SPDX-License-Identifier: AGPL-3.0-or-later
// XML wire sequence derived from penumbra (Shomy 2025-2026, AGPL-3.0-or-later).
using System.Text;
using System.Xml.Linq;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Internals;
using GeekFlashCore.Protocol.Mtk.Loaders;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal sealed class XmlSession(MtkWire wire, MtkProtocolOptions options) : IMtkDaSession
{
    public MtkDaKind Kind => MtkDaKind.Xml;
    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);
    public void Ack(string? value = null) => wire.SendFrame(Encoding.ASCII.GetBytes(value is null ? "OK\0" : "OK@" + value + "\0"));
    private string ReceiveText(int maximum = 65536) => new UTF8Encoding(false, true).GetString(wire.ReadSmallFrame(Math.Min(maximum, options.MaximumXmlSize))).TrimEnd('\0');
    private XElement ReceiveXml() => MtkXmlCodec.Parse(wire.ReadSmallFrame(options.MaximumXmlSize), options.MaximumXmlSize);
    private void Require(string command, XElement root)
    {
        if (MtkXmlCodec.Value(root, "command") != "CMD:" + command)
            throw wire.Failure();
        var result = root.Descendants("result").ToArray();
        if (result.Length > 1 || command == "END" && result.Length != 1 || result.Length == 1 && result[0].Value != "OK")
            throw wire.Failure();
    }
    private void ReadAck()
    {
        string ack = ReceiveText(64);
        if (ack is not ("OK" or "OK@0x0"))
            throw wire.Failure();
    }
    public void Lifetime(string name)
    {
        var root = ReceiveXml();
        Require(name, root);
        Ack();
    }
    public void Begin(string name, IReadOnlyDictionary<string, string> parameters)
    {
        byte[] data = MtkXmlCodec.Create(name, parameters); // Validate before consuming START.
        Lifetime("START");
        wire.SendFrame(data);
        ReadAck();
    }
    private void Simple(string name, Dictionary<string, string> parameters)
    {
        Begin(name, parameters);
        Lifetime("END");
    }
    public void Initialize(MtkDaImage image, MtkEmiImage? emi, MtkTargetInfo target,
        Func<MtkExploitStage, MtkDaImage> checkpoint)
    {
        wire.Stage = MtkBootStage.Da1;
        Simple("SET-RUNTIME-PARAMETER", Args(("checksum_level", "NONE"), ("battery_exist", "AUTO-DETECT"), ("da_log_level", "INFO"),
            ("log_channel", "UART"), ("system_os", "LINUX"), ("initialize_dram", "YES")));
        Simple("HOST-SUPPORTED-COMMANDS", Args(("host_capability", "CMD:DOWNLOAD-FILE^1@CMD:FILE-SYS-OPERATION^1@CMD:PROGRESS-REPORT^1@CMD:UPLOAD-FILE^1@")));
        Begin("NOTIFY-INIT-HW", Args());
        Progress();
        Lifetime("END");
        Simple("SET-HOST-INFO", Args(("info", "GeekFlashCore")));
        image = checkpoint(MtkExploitStage.Da1Ready);
        var region = image.Entry.Regions[image.Entry.EntryRegionIndex + 1];
        long length = region.Length - region.SignatureLength;
        using Stream source = new MtkDataWindow(image.Source, region.FileOffset, length).OpenStream();
        string address = $"0x{region.Address:x}";
        Begin("BOOT-TO", Args(("at_address", address), ("jmp_address", address), ("source_file", "MEM://0x0:0x0")));
        Download(length, source);
        Lifetime("END");
        wire.Stage = MtkBootStage.Da2;
        checkpoint(MtkExploitStage.Da2Ready);
        Simple("HOST-SUPPORTED-COMMANDS", Args(("host_capability", "CMD:DOWNLOAD-FILE^1@CMD:FILE-SYS-OPERATION^1@CMD:PROGRESS-REPORT^1@CMD:UPLOAD-FILE^1@")));
        Begin("NOTIFY-INIT-HW", Args());
        Progress();
        Lifetime("END");
    }
    public byte[]? GetAuthenticationChallenge()
    {
        Begin("GET-SYS-PROPERTY", Args(("key", "DA.SLA"), ("target_file", "MEM://0x0:0x200000")));
        using var property = new MemoryStream();
        Upload(property, null, options.MaximumXmlSize);
        Lifetime("END");
        var document = MtkXmlCodec.Parse(property.ToArray(), options.MaximumXmlSize, allowPropertyKey: true);
        var items = document.DescendantsAndSelf("item").Where(e => (string?)e.Attribute("key") == "DA.SLA").ToArray();
        if (items.Length != 1 || items[0].HasElements)
            throw new MtkResourceException("XML DA.SLA property");
        string state = items[0].Value.Trim();
        if (state == "DISABLED")
            return null;
        if (state != "ENABLED")
            throw wire.Failure();
        Begin("SECURITY-GET-DEV-FW-INFO", Args(("target_file", "MEM://0x0:0x200000")));
        using var output = new MemoryStream();
        byte[]? challenge = null;
        try
        {
            Upload(output, null, options.MaximumXmlSize);
            Lifetime("END");
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
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(output.GetBuffer()); }
    }
    public void Authenticate(ReadOnlySpan<byte> response)
    {
        Begin("SECURITY-SET-FLASH-POLICY", Args(("source_file", "MEM://auth")));
        byte[] copy = response.ToArray();
        try
        {
            using var input = new MemoryStream(copy, false);
            Download(copy.Length, input);
            Lifetime("END");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(copy); }
    }
    public byte[] QueryFile(string command)
    {
        Begin(command, Args(("target_file", "MEM://0x0:0x200000")));
        using var output = new MemoryStream();
        try { Upload(output, null, options.MaximumXmlSize); Lifetime("END"); return output.ToArray(); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(output.GetBuffer()); }
    }
    public byte[] GetSystemProperty(string key)
    {
        Begin("GET-SYS-PROPERTY", Args(("key", key), ("target_file", "MEM://0x0:0x200000")));
        using var output = new MemoryStream();
        try { Upload(output, null, options.MaximumXmlSize); Lifetime("END"); return output.ToArray(); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(output.GetBuffer()); }
    }
    public uint ReadRegister(uint address)
    {
        Begin("READ-REGISTER", Args(("bit_width", "32"), ("base_address", $"0x{address:X}"), ("target_file", "MEM://0x0:0x4")));
        string size = ReceiveText(64);
        if (size != "OK@0x4") throw wire.Failure();
        Ack(); ReadAck(); Ack();
        Span<byte> data = stackalloc byte[4];
        if (wire.ReadFrame(data) != 4) throw wire.Failure();
        Ack(); Lifetime("END"); return BinaryPrimitives.ReadUInt32LittleEndian(data);
    }
    public void WriteRegister(uint address, uint value)
    {
        Begin("WRITE-REGISTER", Args(("bit_width", "32"), ("base_address", $"0x{address:X}"), ("source_file", "MEM://0x0:0x4")));
        using var source = new MemoryStream(MtkWire.Le32(value), false);
        Download(4, source); Lifetime("END");
    }
    public MtkStorageInfo GetStorage()
    {
        Begin("GET-HW-INFO", Args(("target_file", "MEM://0x0:0x200000")));
        using var output = new MemoryStream();
        Upload(output, null, options.MaximumXmlSize);
        Lifetime("END");
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
            if (page is < 512 or > 65536 || (page & (page - 1)) != 0 || spare > page || block < (long)page ||
                block > 16777216 || block % (long)page != 0 || total == 0 || total > long.MaxValue || total % (ulong)block != 0)
                throw new MtkResourceException("XML NAND geometry");
            // XML has no confirmed usable/BMT size or operation type; expose standard reads only.
            var region = new MtkStorageRegion(MtkStorageKind.Nand, 8, "NAND-WHOLE", total, (int)page)
            { CanWrite = false, EraseBlockSize = block };
            return new(MtkStorageKind.Nand, Array.AsReadOnly(new[] { region }), 8, 0) {
                Nand = new(0, (int)page, (int)spare, block, total, total, false) { LogicalCapacityConfirmed = false } };
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
                        _ => $"EMMC-GP{id - 3}"
                    }, size, block));
            }
            if (!regions.Any(r => r.WireId == 8) || rpmb % 256 != 0)
                throw new MtkResourceException("XML eMMC geometry");
            return new(MtkStorageKind.Emmc, regions.AsReadOnly(), 8, checked((uint)(rpmb / 256)));
        }
        if (kind != "UFS")
            throw new MtkCapabilityException("storage kind");
        for (uint i = 0; i < 3; i++)
        {
            ulong size = Number($"lu{i}_size");
            if (size > 0)
                regions.Add(new(MtkStorageKind.Ufs, i + 1, $"UFS-LUA{i}", size, block));
        }
        if (!regions.Any(r => r.WireId == 3))
            throw new MtkResourceException("XML UFS geometry");
        return new(MtkStorageKind.Ufs, regions.AsReadOnly(), 3, 0);
    }
    public void Read(MtkStorageRegion region, long offset, long length, Stream output)
    {
        Begin("READ-FLASH", Args(("partition", region.Name), ("target_file", region.Name), ("length", $"0x{length:X}"), ("offset", $"0x{offset:X}")));
        Upload(output, length, length);
        Lifetime("END");
    }
    public void Write(MtkStorageRegion region, long offset, long length, Stream input)
    {
        if (!region.CanWrite) throw new MtkCapabilityException("read-only storage region");
        Begin("WRITE-FLASH", Args(("partition", region.Name), ("source_file", $"MEM:\\0x0:0x{length:X}"), ("offset", $"0x{offset:X}")));
        FileSize(length);
        Progress();
        Download(length, input);
        Lifetime("END");
    }
    public void Erase(MtkStorageRegion region, long offset, long length)
    {
        if (!region.CanWrite || region.EraseBlockSize == 0) throw new MtkCapabilityException("erase geometry/policy");
        if (offset % region.EraseBlockSize != 0 || length % region.EraseBlockSize != 0) throw new ArgumentOutOfRangeException(nameof(length));
        Begin("ERASE-FLASH", Args(("partition", region.Name), ("length", $"0x{length:X}"), ("offset", $"0x{offset:X}")));
        Progress();
        Lifetime("END");
    }
    public void Reboot(ProtocolRebootMode mode)
    {
        if (mode == ProtocolRebootMode.PowerOff)
            throw new MtkCapabilityException("XML power off");
        if (mode == ProtocolRebootMode.Download)
            Simple("SET-BOOT-MODE", Args(("mode", "FASTBOOT"), ("connect_type", "USB"), ("mobile_log", "OFF"), ("adb", "OFF")));
        Simple("REBOOT", Args(("action", "IMMEDIATE")));
    }
    public long Upload(Stream output, long? expected, long maximum)
    {
        var request = ReceiveXml();
        Require("UPLOAD-FILE", request);
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
        byte[] buffer = ArrayPool<byte>.Shared.Rent(packet);
        try
        {
            for (long done = 0; done < size;)
            {
                ReadAck();
                Ack();
                int want = (int)Math.Min(packet, size - done), n = wire.ReadFrame(buffer.AsSpan(0, want));
                if (n != want)
                    throw wire.Failure();
                output.Write(buffer.AsSpan(0, n));
                Ack();
                done += n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        return size;
    }
    public void Download(long length, Stream input)
    {
        var request = ReceiveXml();
        Require("DOWNLOAD-FILE", request);
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
                wire.SendFrame(buffer.AsSpan(0, n));
                ReadAck();
                done += n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    private int PacketSize(XElement request)
    {
        ulong size = MtkXmlCodec.Number(MtkXmlCodec.Value(request, "packet_length"));
        if (size is 0 or > 1048576 || size > (ulong)options.MaximumFrameSize)
            throw wire.Failure();
        return (int)size; // Must retain the negotiated XML packet boundary.
    }
    private void FileSize(long length)
    {
        var request = ReceiveXml();
        Require("FILE-SYS-OPERATION", request);
        if (MtkXmlCodec.Value(request, "key") != "FILE-SIZE")
            throw wire.Failure();
        if (MtkXmlCodec.Value(request, "file_path") != $"MEM:\\0x0:0x{length:X}")
            throw new MtkResourceException("XML virtual file");
        Ack();
        Ack(length.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
    }
    private void Progress()
    {
        Require("PROGRESS-REPORT", ReceiveXml());
        Ack();
        for (int i = 0; i < options.MaximumProgressEvents; i++)
        {
            string text = ReceiveText(128);
            if (text == "OK!EOT")
            {
                Ack();
                return;
            }
            if (!text.StartsWith("OK!PROGRESS@", StringComparison.Ordinal) ||
                !uint.TryParse(text[12..], out uint percent) || percent > 100)
                throw wire.Failure();
            Ack();
            wire.ProgressPercent?.Invoke((int)percent);
        }
        throw wire.Failure();
    }
}
