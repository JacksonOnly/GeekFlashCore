using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.Gpt.Internals;


internal static class GptFormatValidator
{
    public const int MinimumSectorSize = 512;
    public const int MaximumSectorSize = 65_536;
    public const uint MaximumEntryCount = 16_384;

    public static int ValidateSectorSize(int sectorSize)
    {
        if (sectorSize is < MinimumSectorSize or > MaximumSectorSize ||
            (sectorSize & (sectorSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(
                nameof(sectorSize),
                Strings.InvalidSectorSize);
        return sectorSize;
    }

    public static void ValidateHeaderStructure(
        GptHeader header,
        int? sectorSize = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        uint maximumHeaderSize = sectorSize is int value
            ? checked((uint)value)
            : MaximumSectorSize;
        if (header.HeaderSize < GptCodec.MinimumHeaderSize ||
            header.HeaderSize > maximumHeaderSize)
            throw new GptException(
                Strings.FormatInvalidHeaderSize(header.HeaderSize));
        if (header.PartitionEntryCount == 0 ||
            header.PartitionEntryCount > MaximumEntryCount)
            throw new GptException(
                Strings.FormatInvalidPartitionEntryCount(header.PartitionEntryCount));
        if (header.PartitionEntrySize < GptCodec.MinimumEntrySize ||
            (header.PartitionEntrySize & 7) != 0)
            throw new GptException(
                Strings.FormatInvalidPartitionEntrySize(header.PartitionEntrySize));
        if (header.CurrentLba == ulong.MaxValue ||
            header.AlternateLba == ulong.MaxValue ||
            header.CurrentLba != 0 && header.CurrentLba == header.AlternateLba)
            throw new GptException(
                Strings.FormatInvalidAlternateLba(header.AlternateLba));
        if (header.LastUsableLba != 0 &&
            header.FirstUsableLba > header.LastUsableLba)
            throw new GptException(Strings.InvalidUsableLbaRange);
    }

    public static int GetEntryArrayLength(GptHeader header) =>
        checked((int)header.PartitionEntryCount * (int)header.PartitionEntrySize);

    public static ulong GetSectorCountForBytes(ulong byteCount, int sectorSize) =>
        checked((byteCount + (ulong)sectorSize - 1) / (ulong)sectorSize);

    public static ulong GetPhysicalEntryArraySectorCount(
        int availableEntryCount,
        uint entrySize,
        int sectorSize) =>
        GetSectorCountForBytes(
            checked((ulong)availableEntryCount * entrySize),
            sectorSize);

    public static bool IsHeaderCrcValid(
        ReadOnlySpan<byte> headerBytes,
        GptHeader header)
    {
        int headerSize = checked((int)header.HeaderSize);
        if (headerBytes.Length < headerSize) return false;
        byte[] copy = headerBytes[..headerSize].ToArray();
        copy.AsSpan(16, sizeof(uint)).Clear();
        return Crc32Helper.Compute(copy) == header.HeaderCrc32;
    }

    public static bool IsEntryArrayCrcValid(
        ReadOnlySpan<byte> entries,
        GptHeader header)
    {
        int length = GetEntryArrayLength(header);
        return entries.Length >= length &&
               Crc32Helper.Compute(entries[..length]) == header.PartitionEntryArrayCrc32;
    }

    public static bool HeadersDescribeSameTable(
        GptHeader primary,
        GptHeader backup) =>
        primary.Revision == backup.Revision &&
        primary.HeaderSize == backup.HeaderSize &&
        primary.FirstUsableLba == backup.FirstUsableLba &&
        primary.LastUsableLba == backup.LastUsableLba &&
        primary.DiskId == backup.DiskId &&
        primary.PartitionEntryCount == backup.PartitionEntryCount &&
        primary.PartitionEntrySize == backup.PartitionEntrySize &&
        primary.PartitionEntryArrayCrc32 == backup.PartitionEntryArrayCrc32 &&
        primary.CurrentLba == backup.AlternateLba &&
        primary.AlternateLba == backup.CurrentLba;
}
