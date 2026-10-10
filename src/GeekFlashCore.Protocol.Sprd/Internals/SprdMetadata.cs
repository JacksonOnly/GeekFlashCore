using System.Text;

namespace GeekFlashCore.Protocol.Sprd.Internals;

internal readonly record struct SprdNativePartition(string Name, uint SizeUnits);

internal static class SprdMetadata
{
    private static readonly Encoding Unicode = new UnicodeEncoding(false, false, true);
    internal static SprdLoaderInfo? Loader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        if (bytes.Length < 8) throw new SprdProtocolException(SprdCommand.Execute);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        bool disable = false; byte old = 0, raw = 0; uint flush = 0, storage = 0;
        if (version == 0x7477656e) // "newt" TLV container
        {
            int position = 4; var seen = new HashSet<ushort>();
            while (position < bytes.Length)
            {
                if (bytes.Length - position < 4) throw new SprdProtocolException(SprdCommand.Execute);
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(position + 2)..]); position += 4;
                if (length > bytes.Length - position || !seen.Add(type)) throw new SprdProtocolException(SprdCommand.Execute);
                var value = bytes.Slice(position, length);
                switch (type)
                {
                    case 0:
                        if (length != 4) throw new SprdProtocolException(SprdCommand.Execute);
                        uint flag = BinaryPrimitives.ReadUInt32LittleEndian(value);
                        if (flag > 1) throw new SprdProtocolException(SprdCommand.Execute);
                        disable = flag == 1; break;
                    case 2:
                        if (length != 1) throw new SprdProtocolException(SprdCommand.Execute);
                        raw = value[0]; break;
                    case 3:
                    case 6:
                        if (length != 4) throw new SprdProtocolException(SprdCommand.Execute);
                        uint number = BinaryPrimitives.ReadUInt32LittleEndian(value);
                        if (type == 3) flush = number; else storage = number;
                        break;
                }
                position = checked(position + length);
            }
        }
        else
        {
            // Legacy v1/v2 prefixes and the observed v4 fixed 256-byte DA_INFO_T record.
            // Do not interpret arbitrary versions or truncated v4 capability records.
            if (!(version is 1 or 2 && bytes.Length is 8 or 12 or 16 or 20 or 256 ||
                version == 4 && bytes.Length == 256))
                throw new SprdProtocolException(SprdCommand.Execute);
            uint flag = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            if (flag > 1) throw new SprdProtocolException(SprdCommand.Execute);
            disable = flag == 1;
            if (bytes.Length >= 12) { old = bytes[8]; raw = bytes[9]; }
            if (bytes.Length >= 16) flush = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
            if (bytes.Length >= 20) storage = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        }
        return new(version, disable, old, raw, flush, storage);
    }
    internal static byte[] Selector(string name, long length, SprdPartitionLengthEncoding encoding)
    {
        SprdProtocolOptions.ValidatePartitionName(name);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (encoding == SprdPartitionLengthEncoding.UInt32 && length > uint.MaxValue)
            throw new NotSupportedException(Strings.WidthUnsupported);
        byte[] data = new byte[encoding switch { SprdPartitionLengthEncoding.UInt32 => 76, SprdPartitionLengthEncoding.UInt64 => 80, _ => 88 }];
        Unicode.GetBytes(name, data.AsSpan(0, 72));
        if (encoding == SprdPartitionLengthEncoding.UInt32) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(72), (uint)length);
        else BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(72), checked((ulong)length));
        return data;
    }
    internal static IReadOnlyList<SprdNativePartition> PartitionRecords(ReadOnlySpan<byte> data, SprdProtocolOptions options)
    {
        if (data.Length == 0 || data.Length % 76 != 0 || data.Length / 76 > options.MaximumPartitions)
            throw new SprdProtocolException(SprdCommand.ReadPartition);
        var result = new List<SprdNativePartition>(data.Length / 76); var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (int offset = 0; offset < data.Length; offset += 76)
            {
                string field = Unicode.GetString(data.Slice(offset, 72));
                int end = field.IndexOf('\0');
                if (end < 0 || field.AsSpan(end).IndexOfAnyExcept('\0') >= 0) throw new SprdProtocolException(SprdCommand.ReadPartition);
                string name = field[..end];
                SprdProtocolOptions.ValidatePartitionName(name);
                uint units = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 72)..]);
                if (units == 0 || !names.Add(name)) throw new SprdProtocolException(SprdCommand.ReadPartition);
                result.Add(new(name, units));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        { throw new SprdProtocolException(SprdCommand.ReadPartition); }
        return result.AsReadOnly();
    }
    internal static IReadOnlyList<SprdPartition> ScalePartitions(IReadOnlyList<SprdNativePartition> records, long unitBytes)
    {
        var result = new List<SprdPartition>(records.Count);
        try
        {
            foreach (var record in records) result.Add(new(record.Name, checked(record.SizeUnits * unitBytes)));
        }
        catch (OverflowException) { throw new SprdProtocolException(SprdCommand.ReadPartition); }
        return result.AsReadOnly();
    }
}
