// SPDX-License-Identifier: AGPL-3.0-or-later
// DA extension ABI derived from penumbra/mtk-payloads (Shomy 2025-2026, AGPL-3.0-or-later).
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Boots explicitly prepared Penumbra extensions or validates an existing extension; performs no DA patching.</summary>
public sealed partial class MtkDaExtension : IMtkRpmbService, IMtkRpmbEraseService
{
    private readonly IMtkProtocol _protocol;
    private readonly IMtkSessionAccess _access;
    private long _generation = -1;
    private MtkExtensionContext? _context;
    private readonly HashSet<uint> _authenticated = [];
    public MtkDaExtension(IMtkProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        _protocol = protocol;
        _access = protocol as IMtkSessionAccess ?? throw new MtkCapabilityException("scoped DA channel");
    }
    public bool IsReady => _context is not null && _protocol.IsConnected && _generation == _protocol.Generation;
    /// <summary>Current-generation extension support; RPMB still requires confirmed capacity and authentication.</summary>
    public MtkCapabilities Capabilities => !IsReady ? _protocol.Capabilities : _protocol.Capabilities with
    {
        Memory = MtkCapabilitySupport.Supported,
        Crypto = _context!.SejBase != 0 ? MtkCapabilitySupport.Supported : MtkCapabilitySupport.Unknown,
        Rpmb = MtkCapabilitySupport.Supported
    };
    public bool IsAuthenticated(uint region)
    {
        lock (_authenticated)
            return IsReady && _authenticated.Contains(region);
    }
    /// <summary>Validates an already running extension without uploading code.</summary>
    public void Initialize(MtkExtensionContext context, CancellationToken cancellationToken = default) =>
        Initialize(context, null, 0, cancellationToken);

    /// <summary>Boots explicitly prepared extension bytes, then requires ACK and matching DA context. No patching is performed.</summary>
    public void Load(MtkExtensionContext context, uint address, IDataSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (address == 0 || address % 4 != 0 || source.Length is <= 0 or > 1048576 ||
            (ulong)address + (ulong)source.Length > (ulong)uint.MaxValue + 1 ||
            context is null || (ulong)address < (ulong)context.Da2Base + context.Da2Size && (ulong)context.Da2Base < (ulong)address + (ulong)source.Length)
            throw new ArgumentOutOfRangeException(nameof(address));
        using Stream stream = source.OpenStream();
        if (!stream.CanRead || !stream.CanSeek || stream.Position != 0 || stream.Length != source.Length)
            throw new MtkResourceException("extension source");
        byte[] bytes = new byte[checked((int)source.Length)];
        try { stream.ReadExactly(bytes); Initialize(context, bytes, address, cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private void Initialize(MtkExtensionContext context, byte[]? payload, uint address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Capture borrowed mutable lists before entering the gate or touching the wire.
        context = context with
        {
            UfsRpmbDataBlocks = Capture(context.UfsRpmbDataBlocks, 4),
            AllowedMemoryRanges = Capture(context.AllowedMemoryRanges, 256)
        };
        if (context.Da2Base == 0 || context.Da2Size == 0 || (ulong)context.Da2Base + context.Da2Size > (ulong)uint.MaxValue + 1 ||
            context.UfsRpmbDataBlocks is null || context.UfsRpmbDataBlocks.Count > 4 || !Enum.IsDefined(context.Abi) ||
            context.AllowedMemoryRanges is null || context.AllowedMemoryRanges.Count > 256 ||
            context.AllowedMemoryRanges.Any(r => !r.Contains(r.Address, r.Length)))
            throw new ArgumentOutOfRangeException(nameof(context));
        _access.UseSession(c =>
        {
            if (c.Target.HardwareCode != context.HardwareCode || c.Kind == MtkDaKind.Legacy ||
                c.Storage.Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs))
                throw new MtkCapabilityException("extension profile/dialect");
            var da2 = c.DownloadAgent.Entry.Regions[c.DownloadAgent.Entry.EntryRegionIndex + 1];
            if (context.Da2Base != da2.Address || context.Da2Size != da2.Length - da2.SignatureLength)
                throw new MtkResourceException("extension DA2 context");
            lock (_authenticated)
                _authenticated.Clear();
            _context = null;
            _generation = -1;
            if (payload is not null)
            {
                if (c.Kind == MtkDaKind.XFlash)
                {
                    c.SendCommand((uint)MtkXFlashCommand.BootTo);
                    byte[] range = new byte[16];
                    BinaryPrimitives.WriteUInt64LittleEndian(range, address);
                    BinaryPrimitives.WriteUInt64LittleEndian(range.AsSpan(8), (ulong)payload.Length);
                    c.SendData(range); c.SendData(payload); c.CheckStatus();
                    Span<byte> status = stackalloc byte[4];
                    if (c.ReceiveData(status) != 4 || BinaryPrimitives.ReadUInt32LittleEndian(status) is not (0 or 0x434e5953))
                        throw new MtkProtocolException(MtkBootStage.Da2, (uint)MtkXFlashCommand.BootTo);
                }
                else
                {
                    c.BeginXmlCommand("BOOT-TO", Args(("at_address", Hex(address)), ("jmp_address", Hex(address)), ("source_file", "MEM://0x0:0x0")));
                    using var input = new MemoryStream(payload, false); c.SendXmlFile(input, payload.Length); c.EndXmlCommand();
                }
            }
            if (c.Kind == MtkDaKind.XFlash)
            {
                Control(c, MtkXFlashCommand.ExtAck);
                Span<byte> ack = stackalloc byte[4];
                if (c.ReceiveData(ack) != 4 || BinaryPrimitives.ReadUInt32LittleEndian(ack) != 0)
                    throw new MtkProtocolException(MtkBootStage.Da2, (uint)MtkXFlashCommand.ExtAck);
                c.CheckStatus();
                byte[] bytes = new byte[32];
                uint[] fields = [context.SejBase, context.TzccBase, context.Da2Base, context.Da2Size, (uint)c.WritePacketLength, (uint)c.ReadPacketLength, (uint)c.Storage.Kind, 0];
                for (int i = 0; i < fields.Length; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), fields[i]);
                Control(c, MtkXFlashCommand.ExtSetupDaCtx, bytes);
            }
            else
            {
                c.BeginXmlCommand(MtkXmlCommand.ExtAck, Args());
                using var response = new MemoryStream();
                c.ReceiveXmlFile(response, null, 4096);
                c.EndXmlCommand();
                if (XmlValue(response.ToArray(), "status") != "OK")
                    throw new MtkProtocolException(MtkBootStage.Da2, (uint)MtkXFlashCommand.ExtAck);
                c.BeginXmlCommand(MtkXmlCommand.ExtDaCtx, Args(("sej_base", Hex(context.SejBase)), ("tzcc_base", Hex(context.TzccBase)),
                    ("ssr_base", Hex(context.SsrBase)), ("da2_base", Hex(context.Da2Base)), ("da2_size", Hex(context.Da2Size)),
                    ("storage", c.Storage.Kind == MtkStorageKind.Emmc ? "EMMC" : "UFS"), ("usb_log", "no")));
                c.EndXmlCommand();
            }
            _context = context;
            _generation = c.Generation;
            return 0;
        }, cancellationToken);
    }
    private static T[] Capture<T>(IReadOnlyList<T>? values, int maximum)
    {
        if (values is null || values.Count < 0 || values.Count > maximum)
            throw new ArgumentOutOfRangeException(nameof(values));
        int count = values.Count;
        if (count < 0 || count > maximum)
            throw new ArgumentOutOfRangeException(nameof(values));
        var result = new T[count];
        for (int i = 0; i < count; i++)
            result[i] = values[i];
        return result;
    }
    private void Ready(IMtkDaChannel channel)
    {
        if (!IsReady || _generation != channel.Generation)
            throw new InvalidOperationException(Localization.Strings.ExtensionUnavailable);
    }
    private static byte[] LE(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }
    private static string Hex(uint value) => $"0x{value:X}";
    private static Dictionary<string, string> Args(params (string Key, string Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);
    private static void Control(IMtkDaChannel c, MtkXFlashCommand command, params byte[][] parameters)
    {
        c.SendCommand((uint)MtkXFlashCommand.DeviceCtrl);
        c.SendCommand((uint)command);
        if (parameters.Length > 0)
        {
            foreach (var p in parameters)
                c.SendData(p);
            c.CheckStatus();
        }
    }
    private static void Upload(IMtkDaChannel c, long length, Stream output)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1048576);
        try
        {
            while (length > 0)
            {
                int n = c.ReceiveData(buffer.AsSpan(0, (int)Math.Min(length, 1048576)));
                if (n <= 0)
                    throw new MtkResourceException("extension short read");
                output.Write(buffer.AsSpan(0, n));
                c.SendData(LE(0));
                c.CheckStatus();
                length -= n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    private static void Download(IMtkDaChannel c, long length, Stream input, int limit = 32768)
    {
        int packet = Math.Min(c.WritePacketLength, limit);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(packet);
        try
        {
            while (length > 0)
            {
                int n = (int)Math.Min(packet, length);
                input.ReadExactly(buffer.AsSpan(0, n));
                uint sum = 0;
                foreach (byte b in buffer.AsSpan(0, n))
                    sum += b;
                c.SendData(LE(0));
                c.SendData(LE(sum & 0xffff));
                c.SendData(buffer.AsSpan(0, n));
                c.CheckStatus();
                length -= n;
            }
            c.CheckStatus();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
    private uint Capacity(IMtkDaChannel c, uint region)
    {
        Ready(c);
        if (region > 3 || c.Storage.Kind == MtkStorageKind.Emmc && region != 0)
            throw new ArgumentOutOfRangeException(nameof(region));
        uint size = c.Storage.Kind == MtkStorageKind.Emmc ? c.Storage.RpmbDataBlocks :
            region < _context!.UfsRpmbDataBlocks.Count ? _context.UfsRpmbDataBlocks[(int)region] : 0;
        if (size == 0)
            throw new MtkCapabilityException("RPMB capacity");
        return size;
    }
    public void Authenticate(uint region, ReadOnlySpan<byte> key, CancellationToken cancellationToken = default)
    {
        if (key.Length != 32)
            throw new ArgumentOutOfRangeException(nameof(key));
        byte[] copy = key.ToArray();
        try
        {
            _access.UseSession(c =>
            {
                _ = Capacity(c, region);
                if (c.Kind == MtkDaKind.XFlash)
                {
                    Control(c, MtkXFlashCommand.ExtRpmbInit, LE(region), copy);
                    c.CheckStatus();
                }
                else
                {
                    c.BeginXmlCommand(MtkXmlCommand.ExtRpmbInit, Args(("partition", region.ToString()), ("key", Convert.ToHexString(copy))));
                    c.EndXmlCommand();
                }
                lock (_authenticated)
                    _authenticated.Add(region);
                return 0;
            }, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }
    private long RpmbRange(IMtkDaChannel c, uint region, uint start, uint count)
    {
        uint capacity = Capacity(c, region);
        if (count == 0 || (ulong)start + count > capacity)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!IsAuthenticated(region))
            throw new MtkCapabilityException("RPMB authentication");
        return checked((long)count * 256);
    }
    public void Read(uint region, uint startBlock, uint blockCount, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _access.UseSession(c =>
        {
            long length = RpmbRange(c, region, startBlock, blockCount);
            if (!destination.CanWrite)
                throw new ArgumentException(nameof(destination));
            if (c.Kind == MtkDaKind.XFlash)
            {
                Control(c, MtkXFlashCommand.ExtRpmbRead, LE(region), Range(startBlock, blockCount));
                Upload(c, length, destination);
                c.CheckStatus();
            }
            else
            {
                c.BeginXmlCommand(MtkXmlCommand.ExtRpmbRead, RpmbArgs(region, startBlock, blockCount));
                c.ReceiveXmlFile(destination, length, length);
                c.EndXmlCommand();
            }
            return 0;
        }, cancellationToken);
    }
    public void Write(uint region, uint startBlock, uint blockCount, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        _access.UseSession(c =>
        {
            long length = RpmbRange(c, region, startBlock, blockCount);
            if (!source.CanRead || source.CanSeek && source.Length - source.Position != length)
                throw new MtkResourceException("RPMB source length");
            if (c.Kind == MtkDaKind.XFlash)
            {
                Control(c, MtkXFlashCommand.ExtRpmbWrite, LE(region), Range(startBlock, blockCount));
                Download(c, length, source);
                c.CheckStatus();
            }
            else
            {
                c.BeginXmlCommand(MtkXmlCommand.ExtRpmbWrite, RpmbArgs(region, startBlock, blockCount));
                c.SendXmlFile(source, length);
                c.EndXmlCommand();
            }
            return 0;
        }, cancellationToken); // Unknown writes are never retried.
    }
    private static byte[] Range(uint start, uint count)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, start);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), count);
        return b;
    }
    /// <summary>Writes zero data blocks through the authenticated RPMB backend with bounded buffers.
    /// Counter, capacity and final status rules are identical to Write; unknown writes are never retried.</summary>
    public void Erase(uint region, uint startBlock, uint blockCount, CancellationToken cancellationToken = default)
    {
        using var source = new ZeroStream(checked((long)blockCount * 256));
        Write(region, startBlock, blockCount, source, cancellationToken);
    }
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override int Read(Span<byte> data)
        {
            int count = (int)Math.Min(data.Length, length - _position);
            data[..count].Clear(); _position += count; return count;
        }
        public override int Read(byte[] b, int o, int n) => Read(b.AsSpan(o, n));
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }
    private static Dictionary<string, string> RpmbArgs(uint region, uint start, uint count) => Args(("partition", region.ToString()), ("start_sector", start.ToString()), ("sectors_count", count.ToString()));
    private void Memory(IMtkDaChannel c, uint address, uint length)
    {
        Ready(c);
        if (!_context!.AllowedMemoryRanges.Any(r => r.Contains(address, length)))
            throw new MtkCapabilityException("memory access range");
    }
    /// <summary>Reads one aligned register inside an explicitly approved range. XML uses its memory command.</summary>
    public uint ReadRegister(uint address, CancellationToken cancellationToken = default) =>
        _access.UseSession(c =>
        {
            if (address % 4 != 0)
                throw new ArgumentOutOfRangeException(nameof(address));
            Memory(c, address, 4);
            Span<byte> data = stackalloc byte[4];
            if (c.Kind == MtkDaKind.XFlash)
            {
                Control(c, MtkXFlashCommand.ExtReadRegister, LE(address));
                if (c.ReceiveData(data) != 4)
                    throw new MtkResourceException("register read");
                c.CheckStatus();
            }
            else
            {
                c.BeginXmlCommand(MtkXmlCommand.ExtReadMem, Args(("address", Hex(address)), ("length", "0x4")));
                using var output = new MemoryStream();
                c.ReceiveXmlFile(output, 4, 4);
                c.EndXmlCommand();
                output.ToArray().CopyTo(data);
            }
            return BinaryPrimitives.ReadUInt32LittleEndian(data);
        }, cancellationToken);
    /// <summary>Writes one aligned register inside an explicitly approved range.</summary>
    public void WriteRegister(uint address, uint value, CancellationToken cancellationToken = default) =>
        _access.UseSession(c =>
        {
            if (address % 4 != 0)
                throw new ArgumentOutOfRangeException(nameof(address));
            Memory(c, address, 4);
            if (c.Kind == MtkDaKind.XFlash)
                Control(c, MtkXFlashCommand.ExtWriteRegister, LE(address), LE(value));
            else
            {
                c.BeginXmlCommand(MtkXmlCommand.ExtWriteMem, Args(("address", Hex(address)), ("length", "0x4")));
                using var input = new MemoryStream(LE(value), false);
                c.SendXmlFile(input, 4);
                c.EndXmlCommand();
            }
            return 0;
        }, cancellationToken);
    /// <summary>Explicitly requests the existing extension's RPMB derivation; disposal of the owned result clears it.</summary>
    public MtkSensitiveBuffer DeriveRpmbKey(CancellationToken cancellationToken = default) =>
        _access.UseSession(c =>
        {
            Ready(c);
            if(_context!.Abi==MtkExtensionAbi.Penumbra2)return DeriveKeyCore(c,MtkKeyDeriveId.Rpmb,MtkKeySize.Key256,[],[]);
            if (_context!.SejBase == 0 && _context.TzccBase == 0 && _context.SsrBase == 0)
                throw new MtkCapabilityException("key derivation profile");
            byte[] key = new byte[32];
            try
            {
                if (c.Kind == MtkDaKind.XFlash)
                {
                    Control(c, MtkXFlashCommand.ExtKeyDerive, LE(0));
                    if (c.ReceiveData(key) != 32)
                        throw new MtkResourceException("RPMB derived key");
                    c.CheckStatus();
                }
                else
                {
                    c.BeginXmlCommand(MtkXmlCommand.ExtKeyDerive, Args(("key_type", "RPMB")));
                    using var output = new MemoryStream();
                    byte[]? xml = null;
                    try
                    {
                        c.ReceiveXmlFile(output, null, 4096);
                        c.EndXmlCommand();
                        xml = output.ToArray();
                        string value = XmlValue(xml, "result");
                        if (value.Length != 64)
                            throw new MtkResourceException("RPMB derived key");
                        byte[] decoded = Convert.FromHexString(value);
                        try
                        {
                            decoded.CopyTo(key, 0);
                        }
                        finally { CryptographicOperations.ZeroMemory(decoded); }
                    }
                    finally
                    {
                        if (xml is not null) CryptographicOperations.ZeroMemory(xml);
                        CryptographicOperations.ZeroMemory(output.GetBuffer());
                    }
                }
                return new MtkSensitiveBuffer(key);
            }
            catch { CryptographicOperations.ZeroMemory(key); throw; }
        }, cancellationToken);
    public void ReadMemory(uint address, uint length, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _access.UseSession(c =>
        {
            Memory(c, address, length);
            if (!destination.CanWrite)
                throw new ArgumentException(nameof(destination));
            if (c.Kind == MtkDaKind.XFlash)
            {
                Control(c, MtkXFlashCommand.ExtReadMem, MemoryParams(address, length));
                Upload(c, length, destination);
                c.CheckStatus();
            }
            else
            {
                c.BeginXmlCommand(MtkXmlCommand.ExtReadMem, Args(("address", Hex(address)), ("length", Hex(length))));
                c.ReceiveXmlFile(destination, length, length);
                c.EndXmlCommand();
            }
            return 0;
        }, cancellationToken);
    }
    public void WriteMemory(uint address, uint length, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        _access.UseSession(c =>
        {
            Memory(c, address, length);
            if (!source.CanRead || source.CanSeek && source.Length - source.Position != length)
                throw new MtkResourceException("memory source");
            if (c.Kind == MtkDaKind.XFlash)
            {
                Control(c, MtkXFlashCommand.ExtWriteMem, MemoryParams(address, length));
                Download(c, length, source, c.WritePacketLength);
                c.CheckStatus();
            }
            else
            {
                c.BeginXmlCommand(MtkXmlCommand.ExtWriteMem, Args(("address", Hex(address)), ("length", Hex(length))));
                c.SendXmlFile(source, length);
                c.EndXmlCommand();
            }
            return 0;
        }, cancellationToken);
    }
    private static byte[] MemoryParams(uint address, uint length)
    {
        byte[] b = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(b, address);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), length);
        return b;
    }
    /// <summary>Runs bounded SEJ crypto only after context confirmation. The caller owns and must clear the result.</summary>
    public byte[] TransformSej(ReadOnlySpan<byte> data, bool encrypt, bool antiClone = true, bool legacy = false, bool xor = false)
    {
        if (data.Length == 0 || data.Length > 65536 || data.Length % 16 != 0)
            throw new ArgumentOutOfRangeException(nameof(data));
        byte[] copy = data.ToArray();
        try
        {
            return _access.UseSession(c => TransformSej(c, copy, encrypt, antiClone, legacy, xor));
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }
    internal byte[] TransformSej(IMtkDaChannel c, ReadOnlySpan<byte> data, bool encrypt, bool antiClone, bool legacy, bool xor)
    {
        if (data.IsEmpty || data.Length > 65536 || data.Length % 16 != 0)
            throw new ArgumentOutOfRangeException(nameof(data));
        Ready(c);
        if(_context!.Abi==MtkExtensionAbi.Penumbra2)return TransformSejCore(c,data,new(encrypt,antiClone,legacy,xor));
        if (_context!.SejBase == 0)
            throw new MtkCapabilityException("SEJ profile");
        byte[] copy = data.ToArray(), result = new byte[data.Length];
        try
        {
            using var input = new MemoryStream(copy, false);
            using var output = new MemoryStream(result, true);
            if (c.Kind == MtkDaKind.XFlash)
            {
                byte[] p = new byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)copy.Length);
                p[4] = encrypt ? (byte)1 : (byte)0;
                p[5] = antiClone ? (byte)1 : (byte)0;
                p[6] = xor ? (byte)1 : (byte)0;
                p[7] = legacy ? (byte)1 : (byte)0;
                p[8] = 1;
                p[10] = 2;
                Control(c, MtkXFlashCommand.ExtSej, p);
                Download(c, copy.Length, input, c.WritePacketLength);
                Upload(c, copy.Length, output);
                c.CheckStatus();
            }
            else
            {
                if (legacy || xor)
                    throw new MtkCapabilityException("XML legacy SEJ");
                c.BeginXmlCommand(MtkXmlCommand.ExtSej, Args(("encrypt", encrypt ? "yes" : "no"), ("ac", antiClone ? "yes" : "no"), ("length", Hex((uint)copy.Length))));
                c.SendXmlFile(input, copy.Length);
                c.ReceiveXmlFile(output, copy.Length, copy.Length);
                c.EndXmlCommand();
            }
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }
    private static string XmlValue(byte[] bytes, string field)
    {
        if (bytes.Length is < 1 or > 4096)
            throw new MtkResourceException("extension XML");
        try
        {
            using var reader = XmlReader.Create(new StringReader(new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0')),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 0 });
            XElement root = XElement.Load(reader);
            var nodes = root.DescendantsAndSelf().ToArray();
            if (nodes.Length > 1024 || nodes.Any(n => n.Name.NamespaceName.Length != 0 || n.Ancestors().Count() > 8 ||
                n.Attributes().Any(a => a.IsNamespaceDeclaration || a.Name.NamespaceName.Length != 0)))
                throw new MtkResourceException("extension XML");
            var items = nodes.Where(n => n.Name.LocalName == field).ToArray();
            if (items.Length != 1 || items[0].HasElements)
                throw new MtkResourceException("extension XML");
            return items[0].Value;
        }
        catch (Exception ex) when (ex is XmlException or System.Text.DecoderFallbackException)
        {
            throw new MtkResourceException("extension XML");
        }
    }
}
