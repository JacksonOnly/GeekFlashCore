using GeekFlashCore.Gpt;
using GeekFlashCore.Gpt.Abstractions;
using GeekFlashCore.Protocol.Sprd.Internals;
using GeekFlashCore.Shared.Utilities;
using Serilog;

namespace GeekFlashCore.Protocol.Sprd;

public sealed partial class SprdProtocol
{
    private IReadOnlyList<SprdPartition> GetGptPartitionsCore(bool detectSource = false)
    {
        int length = _options.GptReadBytes;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            // A confirmed prefix window is not a guessed disk capacity or a whole-disk view.
            using var output = new MemoryStream(buffer, 0, length, writable: true, publiclyVisible: false);
            if (detectSource)
            {
                var start = _wire.Command(SprdCommand.ReadStart,
                    SprdMetadata.Selector("user_partition", length, _options.PartitionLengthEncoding));
                if (start.Type != SprdCommand.Ack)
                {
                    if (start.Type is not (SprdCommand.OperationFailed or SprdCommand.UnsupportedCommand) || !start.Data.IsEmpty)
                        throw new SprdProtocolException(SprdCommand.ReadStart, start.Type);
                    _wire.Expect(SprdCommand.ReadEnd);
                    Log.ForContext<SprdProtocol>().Warning(Strings.GptQueryUnavailable);
                    return GetNativePartitionsCore();
                }
            }
            ReadCore(new("user_partition", length), 0, length, output, null, readStarted: detectSource);
            _wire.Check();
            var bytes = buffer.AsSpan(0, length);
            if (detectSource && !HasGptEvidence(bytes)) return GetNativePartitionsCore();
            IReadOnlyList<SprdPartition>? result = null; int selectedSector = 0;
            if (_options.GptSectorSize is int explicitSector)
            { result = ParseGptPartitions(bytes, explicitSector); selectedSector = explicitSector; }
            else
            {
                // These are offline interpretations of the same bytes, not device retries.
                ReadOnlySpan<int> candidates = [512, 4096];
                foreach (int candidate in candidates)
                {
                    _wire.Check(); IReadOnlyList<SprdPartition> parsed;
                    try { parsed = ParseGptPartitions(bytes, candidate); }
                    catch (SprdProtocolException) { continue; }
                    if (result is not null) throw new SprdProtocolException(SprdCommand.ReadStart);
                    result = parsed; selectedSector = candidate;
                }
                if (result is null) throw new SprdProtocolException(SprdCommand.ReadStart);
            }
            _wire.Check(); SetPartitionSource(SprdPartitionTableSource.UserPartitionGpt, selectedSector);
            Log.ForContext<SprdProtocol>().Information(Strings.GptSectorDetected, selectedSector);
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    private static bool HasGptEvidence(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 520 && bytes.Slice(512, 8).SequenceEqual("EFI PART"u8) ||
            bytes.Length >= 4104 && bytes.Slice(4096, 8).SequenceEqual("EFI PART"u8)) return true;
        if (bytes.Length < 512 || bytes[510] != 0x55 || bytes[511] != 0xaa) return false;
        for (int index = 0; index < 4; index++) if (bytes[446 + index * 16 + 4] == 0xee) return true;
        return false;
    }
    private IReadOnlyList<SprdPartition> ParseGptPartitions(ReadOnlySpan<byte> bytes, int sector)
    {
        try
        {
            int length = bytes.Length;
            var header = bytes.Slice(sector, sector);
            if (!header[..8].SequenceEqual("EFI PART"u8)) throw new SprdProtocolException(SprdCommand.ReadStart);
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
            uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
            ulong entryLba = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
            if (headerSize < 92 || headerSize > sector || count == 0 || count > _options.MaximumPartitions ||
                entrySize < 128 || entrySize > 4096 || (entrySize & 7) != 0 ||
                BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) != 1 || entryLba < 2 ||
                entryLba > (ulong)(length / sector) ||
                (ulong)count * entrySize > (ulong)length - entryLba * (ulong)sector)
                throw new SprdProtocolException(SprdCommand.ReadStart);
            // Verify this exact primary header before a general parser can choose another candidate.
            Span<byte> copy = stackalloc byte[sector]; header[..(int)headerSize].CopyTo(copy); copy.Slice(16, 4).Clear();
            if (Crc32Helper.Compute(copy[..(int)headerSize]) != BinaryPrimitives.ReadUInt32LittleEndian(header[16..]))
                throw new SprdProtocolException(SprdCommand.ReadStart);
            ulong arraySectors = ((ulong)count * entrySize + (ulong)sector - 1) / (ulong)sector;
            ulong firstUsable = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
            ulong lastUsable = BinaryPrimitives.ReadUInt64LittleEndian(header[48..]);
            ulong backupLba = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
            if (firstUsable < entryLba + arraySectors || firstUsable > lastUsable ||
                backupLba <= lastUsable || backupLba - lastUsable <= arraySectors)
                throw new SprdProtocolException(SprdCommand.ReadStart);
            var document = new GptParser().Parse(bytes, new()
            {
                SectorSize = sector, CrcPolicy = GptCrcPolicy.Strict, SkipEmptyPartitionTypeId = true,
                AllowEmptyPartitionTypeId = false, AllowUnpatchedPartitionGeometry = false
            });
            if (document.ImageType != GptImageType.Main || document.Header.CurrentLba != 1 ||
                document.Header.HeaderCrc32 != BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) ||
                document.Overlaps.Count != 0 || document.Entries.Count is 0 ||
                document.Entries.Count > _options.MaximumPartitions)
                throw new SprdProtocolException(SprdCommand.ReadStart);
            var result = new List<SprdPartition>(document.Entries.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                _wire.Check(); SprdProtocolOptions.ValidatePartitionName(entry.Name);
                long capacity = checked((long)(entry.SectorCount * (ulong)sector));
                if (capacity <= 0 || !names.Add(entry.Name)) throw new SprdProtocolException(SprdCommand.ReadStart);
                result.Add(new(entry.Name, capacity));
            }
            return result.AsReadOnly();
        }
        catch (Exception exception) when (exception is GptException or ArgumentException or OverflowException)
        { throw new SprdProtocolException(SprdCommand.ReadStart); }
    }
}
