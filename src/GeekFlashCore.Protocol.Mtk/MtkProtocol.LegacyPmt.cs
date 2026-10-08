// Disk-format facts: JacksonOnly/MtkPt README (PT/MPT v1.0), independently implemented.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol
{
    private IReadOnlyList<PartitionInfo> ReadLegacyDiskPmt()
    {
        var region = _storage!.Regions.Single(r => r.WireId == _storage.UserRegionId);
        if (region.Kind != MtkStorageKind.Emmc || region.BlockSize != 512 || region.Length < 0x100000)
            throw new MtkCapabilityException("Legacy eMMC disk PMT v1.0");
        long offset = checked(region.Length - 0x100000);
        byte[] bytes = ReadMetadata(region, offset, 4096);
        bool mirror = false;
        try
        {
            if (!ValidDiskPmtHeader(bytes, 0x50547631))
            {
                CryptographicOperations.ZeroMemory(bytes);
                // Only a completed read with an invalid header permits mirror recovery.
                bytes = ReadMetadata(region, checked(offset + 4096), 4096);
                mirror = true;
                if (!ValidDiskPmtHeader(bytes, 0x4d505431))
                    throw new MtkResourceException("disk PMT copies/version");
            }
            var partitions = ParseDiskPmt(bytes, region, mirror);
            if (mirror) _logger.ForContext("MtkSummary", true).Warning(Strings.PmtMirrorSelected, region.Name);
            return partitions;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static bool ValidDiskPmtHeader(ReadOnlySpan<byte> bytes, uint magic) =>
        bytes.Length == 4096 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == magic &&
        bytes.Slice(4, 4).SequenceEqual("1.0\0"u8) && BinaryPrimitives.ReadUInt32LittleEndian(bytes[0xffc..]) == magic;

    private IReadOnlyList<PartitionInfo> ParseDiskPmt(ReadOnlySpan<byte> bytes, MtkStorageRegion region, bool mirror)
    {
        List<PartitionInfo> partitions = [];
        var encoding = new UTF8Encoding(false, true);
        string sequence = (BinaryPrimitives.ReadUInt32LittleEndian(bytes[0xff8..]) & 0xff).ToString(CultureInfo.InvariantCulture);
        for (int index = 0; index < 40; index++)
        {
            var entry = bytes.Slice(8 + index * 88, 88);
            if (entry.IndexOfAnyExcept((byte)0) < 0) continue;
            var nameBytes = entry[..64];
            int end = nameBytes.IndexOf((byte)0);
            if (end <= 0 || nameBytes[end..].IndexOfAnyExcept((byte)0) >= 0)
                throw new MtkResourceException("disk PMT name");
            string name;
            try { name = encoding.GetString(nameBytes[..end]); }
            catch (DecoderFallbackException) { throw new MtkResourceException("disk PMT name"); }
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                throw new MtkResourceException("disk PMT name");
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(entry[64..]);
            ulong start = BinaryPrimitives.ReadUInt64LittleEndian(entry[72..]);
            ulong flags = BinaryPrimitives.ReadUInt64LittleEndian(entry[80..]);
            if (start > long.MaxValue || size > long.MaxValue) throw new MtkResourceException("disk PMT range");
            _ = Range(new(region.WireId, (long)start, (long)size));
            if (partitions.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                (long)start < p.Offset!.Value + p.Length!.Value && p.Offset.Value < (long)start + (long)size))
                throw new MtkResourceException("disk PMT overlap/name");
            partitions.Add(new(name, (long)start, (long)start, (long)size, new Dictionary<string, string>
            {
                ["PhysicalPartitionNumber"] = region.WireId.ToString(CultureInfo.InvariantCulture),
                ["MaskFlags"] = $"0x{flags:X16}", ["PmtSequence"] = sequence, ["PmtCopy"] = mirror ? "MPT" : "PT"
            }));
        }
        if (partitions.Count == 0) throw new MtkResourceException("disk PMT empty");
        return partitions.AsReadOnly();
    }
}
