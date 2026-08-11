using System.Buffers;
using System.Text;
using GeekFlashCore.Gpt.Internals;
using GeekFlashCore.IO.BlockDevice;

namespace GeekFlashCore.Gpt;

public class GptParser : IGptParser
{

    public IGpt Parse(
        ReadOnlySpan<byte> image,
        GptParseOptions? options = null)
    {
        options ??= new GptParseOptions();
        GptLayout layout = GptLayoutDetector.Detect(
            image,
            options.SectorSize,
            options.HeaderOnly);
        return ParseLayout(layout, options);
    }

    internal static IGpt ParseLayout(
        GptLayout layout,
        GptParseOptions options)
    {
        GptHeaderLayout active = layout.ActiveHeader;
        byte[]? entryStorage = null;
        var entries = new List<GptEntry>();
        if (!options.HeaderOnly)
        {
            (active, entries, entryStorage) = ParseBestCopy(layout, options);
        }
        GptHeader header = active.Header;

        var document = new GuidPartitionTable(
            layout,
            active,
            header,
            entries,
            entryStorage,
            options.IncludeUnallocatedRegions);

        GptCrcStatus status = document.CrcStatus;
        if (options.CrcPolicy == GptCrcPolicy.Strict &&
            (!status.HeaderValid || status.PartitionEntryArrayValid is false))
            throw new GptException(Strings.CrcValidationFailed);
        if (options.RequireRedundantCopiesConsistent &&
            document.SourceRedundancyStatus.CopiesConsistent is false)
            throw new GptException(Strings.RedundantCopiesInconsistent);
        if (options.CrcPolicy == GptCrcPolicy.Repair &&
            (!status.HeaderValid || status.PartitionEntryArrayValid is false))
            document.RepairCrc();
        return document;
    }

    public IGpt Parse(
        Stream stream,
        GptParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException(Strings.InputStreamMustBeReadableAndSeekable, nameof(stream));
        if (stream.Length > int.MaxValue)
            throw new NotSupportedException(Strings.ImageLargerThanTwoGiBUnsupported);

        int length = checked((int)stream.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(rented.AsSpan(0, length));
            return Parse(rented.AsSpan(0, length), options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public IGpt ParseFile(
        string path,
        GptParseOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new GptParseOptions();
        string fullPath = Path.GetFullPath(path);
        long sourceLength;
        using (var source = new FileBlockDevice(fullPath))
        {
            sourceLength = source.Length;
            try
            {
                if (GptFileLayoutDetector.TryDetectFullDisk(
                        source,
                        fullPath,
                        options.SectorSize,
                        options.HeaderOnly,
                        out GptLayout? layout))
                    return ParseLayout(layout, options);
            }
            catch (GptException) when (sourceLength <= int.MaxValue)
            {
                // Compact and sgdisk containers use the span detector below.
            }
        }
        if (sourceLength > int.MaxValue)
            throw new NotSupportedException(Strings.ImageLargerThanTwoGiBUnsupported);

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        return Parse(stream, options);
    }

    private static void ValidatePartitionGeometry(
        GptEntry partition,
        GptHeader header,
        GptParseOptions options,
        int slotIndex)
    {
        if (partition.TypeId == Guid.Empty && !options.AllowEmptyPartitionTypeId)
            throw new GptException(
                Strings.FormatPartitionEntryEmptyTypeId(slotIndex));

        bool isUnpatchedSentinel = partition.FirstLba > 0 &&
            partition.LastLba == partition.FirstLba - 1;
        if (partition.FirstLba > partition.LastLba &&
            !(options.AllowUnpatchedPartitionGeometry && isUnpatchedSentinel))
            throw new GptException(
                Strings.FormatPartitionEntryOutsideUsableRange(slotIndex));

        bool hasUsableRange = header.LastUsableLba >= header.FirstUsableLba &&
                              header.LastUsableLba != 0;
        if (!hasUsableRange)
        {
            if (!options.AllowUnpatchedPartitionGeometry)
                throw new GptException(Strings.InvalidUsableLbaRange);
            return;
        }

        if (!options.AllowUnpatchedPartitionGeometry &&
            (partition.FirstLba < header.FirstUsableLba ||
             !isUnpatchedSentinel && partition.LastLba > header.LastUsableLba))
            throw new GptException(
                Strings.FormatPartitionEntryOutsideUsableRange(slotIndex));
    }

    private static (GptHeaderLayout Active, List<GptEntry> Entries, byte[] Storage)
        ParseBestCopy(
            GptLayout layout,
            GptParseOptions options)
    {
        GptHeaderLayout preferred = layout.ActiveHeader;
        GptHeaderLayout? alternate = preferred.Copy == GptHeaderCopy.Primary
            ? layout.BackupHeader
            : layout.MainHeader;
        GptException? preferredError = null;
        foreach (GptHeaderLayout candidate in EnumerateCandidates(preferred, alternate))
        {
            if (!ReferenceEquals(candidate, preferred) &&
                options.CrcPolicy == GptCrcPolicy.Strict &&
                !candidate.Status.IsUsable)
                continue;
            try
            {
                return (candidate, ParseEntries(candidate, options), candidate.EntryStorage.ToArray());
            }
            catch (GptException exception)
            {
                preferredError ??= exception;
            }
        }
        throw preferredError ?? new GptException(Strings.CrcValidationFailed);
    }

    private static IEnumerable<GptHeaderLayout> EnumerateCandidates(
        GptHeaderLayout preferred,
        GptHeaderLayout? alternate)
    {
        yield return preferred;
        if (alternate is not null) yield return alternate;
    }

    private static List<GptEntry> ParseEntries(
        GptHeaderLayout layout,
        GptParseOptions options)
    {
        GptHeader header = layout.Header;
        int entrySize = checked((int)header.PartitionEntrySize);
        ReadOnlySpan<byte> sourceEntries = layout.EntryStorage.AsSpan(
            0,
            GptFormatValidator.GetEntryArrayLength(header));
        var entries = new List<GptEntry>();
        for (int slotIndex = 0; slotIndex < header.PartitionEntryCount; slotIndex++)
        {
            ReadOnlySpan<byte> source = sourceEntries.Slice(slotIndex * entrySize, entrySize);
            GptEntry partition = ReadPartition(source, slotIndex, options);
            if (partition.TypeId == Guid.Empty && partition.Id == Guid.Empty) continue;
            ValidatePartitionGeometry(partition, header, options, slotIndex);
            entries.Add(new GptEntry(
                entries.Count + 1,
                slotIndex,
                partition.TypeId,
                partition.Id,
                partition.FirstLba,
                partition.LastLba,
                partition.Attributes,
                partition.Name));
        }
        return entries;
    }

    private static GptEntry ReadPartition(
        ReadOnlySpan<byte> source,
        int slotIndex,
        GptParseOptions options)
    {
        try
        {
            return GptCodec.ReadEntry(
                source,
                options.ReplaceInvalidPartitionNameData);
        }
        catch (DecoderFallbackException exception)
        {
            throw new GptException(
                Strings.FormatPartitionNameInvalidOnDisk(slotIndex),
                exception);
        }
    }
}