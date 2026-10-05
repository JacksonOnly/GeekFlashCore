using System.Text;
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Da;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol : IMtkDaDiagnostics
{
    /// <inheritdoc />
    public MtkSensitiveBuffer QueryDa(MtkDaQuery query, CancellationToken cancellationToken = default) => ExecuteSensitive(() =>
    {
        Ready();
        if (!Enum.IsDefined(query))
            throw new ArgumentOutOfRangeException(nameof(query));
        if (_da is XFlashSession x && query != MtkDaQuery.HardwareInfo)
            return x.Control((uint)query, maximum: _options.MaximumFrameSize);
        if (_da is XmlSession xml)
            return query switch
            {
                MtkDaQuery.DeviceFirmwareInfo => xml.QueryFile("SECURITY-GET-DEV-FW-INFO"),
                MtkDaQuery.HardwareInfo => xml.QueryFile("GET-HW-INFO"),
                MtkDaQuery.Version => xml.GetSystemProperty("DA.VERSION"),
                _ => throw new MtkCapabilityException("DA query/dialect")
            };
        if (_da is LegacySession legacy && query == MtkDaQuery.UsbSpeed)
            return [legacy.GetUsbSpeed()];
        throw new MtkCapabilityException("DA query/dialect");
    }, cancellationToken);

    /// <inheritdoc />
    public MtkSensitiveBuffer GetDaSystemProperty(string key, CancellationToken cancellationToken = default) => ExecuteSensitive(() =>
    {
        Ready();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException(nameof(key));
        if (_da is not XmlSession xml)
            throw new MtkCapabilityException("XML system property");
        return xml.GetSystemProperty(key);
    }, cancellationToken);

    private MtkSensitiveBuffer ExecuteSensitive(Func<byte[]> action, CancellationToken token)
    {
        MtkSensitiveBuffer? result = null;
        try { return Execute(() => result = new MtkSensitiveBuffer(action()), token); }
        catch { result?.Dispose(); throw; }
    }

    /// <inheritdoc />
    public uint ReadDaRegister(uint address, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Ready();
        if (address % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(address));
        return _da switch
        {
            LegacySession legacy => legacy.ReadRegister(address),
            XmlSession xml => xml.ReadRegister(address),
            _ => throw new MtkCapabilityException("standard DA register access")
        };
    }, cancellationToken);

    /// <inheritdoc />
    public void WriteDaRegister(uint address, uint value, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Ready();
        if (address % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(address));
        if (_da is LegacySession legacy) legacy.WriteRegister(address, value);
        else if (_da is XmlSession xml) xml.WriteRegister(address, value);
        else throw new MtkCapabilityException("standard DA register access");
        return 0;
    }, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<PartitionInfo> GetLegacyPartitionTable(MtkPmtLayout layout, CancellationToken cancellationToken = default) =>
        Execute(() => ReadLegacyPmt(layout), cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<PartitionInfo> GetXmlPartitionTable(CancellationToken cancellationToken = default) =>
        Execute(ReadXmlPartitionTable, cancellationToken);

    private IReadOnlyList<PartitionInfo> ReadXmlPartitionTable()
    {
        Ready();
        if (_da is not XmlSession xml) throw new MtkCapabilityException("XML partition table");
        byte[] bytes = xml.QueryFile("READ-PARTITION-TABLE");
        try
        {
            var root = MtkXmlCodec.Parse(bytes, _options.MaximumXmlSize, allowPartitionVersion: true);
            if (root.Name != "partition_table" || root.Elements().Any(e => e.Name != "pt"))
                throw new MtkResourceException("XML partition table");
            var region = _storage!.Regions.Single(r => r.WireId == _storage.UserRegionId);
            List<PartitionInfo> partitions = [];
            foreach (var entry in root.Elements("pt"))
            {
                if (entry.Elements().Count() != 3 || entry.Elements().Any(e => e.Name != "name" && e.Name != "start" && e.Name != "size"))
                    throw new MtkResourceException("XML partition entry");
                string name = MtkXmlCodec.Value(entry, "name");
                ulong start = MtkXmlCodec.Number(MtkXmlCodec.Value(entry, "start")), size = MtkXmlCodec.Number(MtkXmlCodec.Value(entry, "size"));
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) || start > long.MaxValue || size > long.MaxValue)
                    throw new MtkResourceException("XML partition range/name");
                _ = Range(new(region.WireId, (long)start, (long)size));
                if (partitions.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    (long)start < p.Offset!.Value + p.Length!.Value && p.Offset.Value < (long)start + (long)size))
                    throw new MtkResourceException("XML partition overlap/name");
                partitions.Add(new(name, (long)start, (long)start, (long)size,
                    new Dictionary<string, string> { ["PhysicalPartitionNumber"] = region.WireId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            }
            return partitions.AsReadOnly();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private IReadOnlyList<PartitionInfo> ReadLegacyPmt(MtkPmtLayout layout)
    {
        Ready();
        if (!Enum.IsDefined(layout)) throw new ArgumentOutOfRangeException(nameof(layout));
        if (_da is not LegacySession legacy) throw new MtkCapabilityException("Legacy PMT");
        byte[] bytes = legacy.ReadPmt();
        int stride = layout switch { MtkPmtLayout.Word32 => 76, MtkPmtLayout.Word64 => 88, _ => 96 };
        if (bytes.Length == 0 || bytes.Length % stride != 0 || bytes.Length / stride > 4096)
            throw new MtkResourceException("PMT length");
        var region = _storage!.Regions.Single(r => r.WireId == _storage.UserRegionId);
        List<PartitionInfo> partitions = [];
        var encoding = new UTF8Encoding(false, true);
        for (int offset = 0; offset < bytes.Length; offset += stride)
        {
            var entry = bytes.AsSpan(offset, stride);
            var nameBytes = entry[..64];
            if (nameBytes.IndexOfAnyExcept((byte)0) < 0) continue;
            int end = nameBytes.IndexOf((byte)0);
            if (end < 0) end = 64;
            if (nameBytes[end..].IndexOfAnyExcept((byte)0) >= 0) throw new MtkResourceException("PMT name");
            string name;
            try { name = encoding.GetString(nameBytes[..end]); }
            catch (DecoderFallbackException) { throw new MtkResourceException("PMT name"); }
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw new MtkResourceException("PMT name");
            ulong size = layout == MtkPmtLayout.Word32 ? BinaryPrimitives.ReadUInt32LittleEndian(entry[64..]) : BinaryPrimitives.ReadUInt64LittleEndian(entry[64..]);
            ulong start = layout == MtkPmtLayout.Word32 ? BinaryPrimitives.ReadUInt32LittleEndian(entry[68..]) :
                BinaryPrimitives.ReadUInt64LittleEndian(entry[(layout == MtkPmtLayout.Legacy96 ? 80 : 72)..]);
            if (start > long.MaxValue || size > long.MaxValue) throw new MtkResourceException("PMT range");
            _ = Range(new(region.WireId, (long)start, (long)size));
            if (partitions.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                (long)start < p.Offset!.Value + p.Length!.Value && p.Offset.Value < (long)start + (long)size))
                throw new MtkResourceException("PMT overlap/name");
            partitions.Add(new(name, (long)start, (long)start, (long)size,
                new Dictionary<string, string> { ["PhysicalPartitionNumber"] = region.WireId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        }
        return partitions.AsReadOnly();
    }
}
