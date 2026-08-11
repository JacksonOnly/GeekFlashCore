using System.Buffers.Binary;
using System.Text;

namespace GeekFlashCore.Gpt.Internals;

internal static class GptCodec
{
    public const int MinimumHeaderSize = 92;
    public const int MinimumEntrySize = 128;
    private const int EntryNameOffset = 56;
    private const int StandardNameByteCount = 72;
    private static ReadOnlySpan<byte> Signature => "EFI PART"u8;

    private static readonly Encoding StrictUnicode =
        new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    private static readonly Encoding ReplacementUnicode =
        new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);

    public static GptHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < MinimumHeaderSize)
            throw new GptException(Strings.HeaderRequiresMinimumBytes);
        if (!source.StartsWith(Signature))
            throw new GptException(Strings.SignatureNotFound);

        return new GptHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[16..20]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[20..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[24..32]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[32..40]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[40..48]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[48..56]),
            new Guid(source[56..72]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[72..80]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[80..84]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[84..88]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[88..92]));
    }

    public static void WriteHeader(Span<byte> destination, GptHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (destination.Length < MinimumHeaderSize)
            throw new ArgumentException(Strings.HeaderDestinationRequiresMinimumBytes, nameof(destination));

        Signature.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], header.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], header.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..20], header.HeaderCrc32);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], header.Reserved);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..32], header.CurrentLba);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..40], header.AlternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[40..48], header.FirstUsableLba);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[48..56], header.LastUsableLba);
        header.DiskId.TryWriteBytes(destination[56..72]);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[72..80], header.PartitionEntryLba);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[80..84], header.PartitionEntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[84..88], header.PartitionEntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[88..92], header.PartitionEntryArrayCrc32);
    }

    public static GptEntry ReadEntry(
        ReadOnlySpan<byte> source,
        bool replaceInvalidNameData = false)
    {
        ValidateEntrySize(source.Length, nameof(source));
        int nameByteCount = Math.Min(StandardNameByteCount, source.Length - EntryNameOffset);
        Encoding nameEncoding = replaceInvalidNameData ? ReplacementUnicode : StrictUnicode;
        string name = nameEncoding.GetString(source.Slice(EntryNameOffset, nameByteCount)).TrimEnd('\0');

        return new GptEntry(0, 0,
            new Guid(source[..16]),
            new Guid(source[16..32]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[32..40]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[40..48]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[48..56]),
            name);
    }

    public static void WriteEntry(Span<byte> destination, GptEntry partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ValidateEntrySize(destination.Length, nameof(destination));
        int nameByteCapacity = Math.Min(StandardNameByteCount, destination.Length - EntryNameOffset);
        int nameByteCount;
        try
        {
            nameByteCount = StrictUnicode.GetByteCount(partition.Name);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(Strings.PartitionNameInvalidUtf16, nameof(partition), exception);
        }

        if (nameByteCount > nameByteCapacity)
            throw new ArgumentException(
                Strings.FormatPartitionNameExceedsLimit(nameByteCapacity / 2),
                nameof(partition));

        destination[..MinimumEntrySize].Clear();
        partition.TypeId.TryWriteBytes(destination[..16]);
        partition.Id.TryWriteBytes(destination[16..32]);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..40], partition.FirstLba);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[40..48], partition.LastLba);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[48..56], partition.Attributes);
        try
        {
            StrictUnicode.GetBytes(partition.Name, destination.Slice(EntryNameOffset, nameByteCount));
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(Strings.PartitionNameInvalidUtf16, nameof(partition), exception);
        }
    }

    private static void ValidateEntrySize(int entrySize, string parameterName)
    {
        if (entrySize < MinimumEntrySize || (entrySize & 7) != 0)
            throw new ArgumentException(
                Strings.EntrySizeInvalid,
                parameterName);
    }
}