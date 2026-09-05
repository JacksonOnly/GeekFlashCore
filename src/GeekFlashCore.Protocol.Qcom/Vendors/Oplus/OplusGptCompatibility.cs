using System.Globalization;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;
using GeekFlashCore.Protocol.Qcom.Firehose.Storage;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

/// <summary>Runtime GPT compatibility evidence; never inferred from a loader's static metadata.</summary>
public sealed class OplusGptCompatibility
{
    private OplusGptCompatibility(long? protectedSector) => ProtectedSector = protectedSector;

    public long? ProtectedSector { get; }

    public static OplusGptCompatibility Probe(FirehoseSession session, uint sectorSize, int bufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (sectorSize is 0 or > 65536) throw new ArgumentOutOfRangeException(nameof(sectorSize));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if (TryRead(session, sectorSize, bufferSize, 5, 31, "BackupGPT", "gpt_backup0.bin", cancellationToken))
            return new OplusGptCompatibility(null);
        bool primary = TryRead(session, sectorSize, bufferSize, 33, 3, "PrimaryGPT", "gpt_main0.bin", cancellationToken);
        return new OplusGptCompatibility(primary ? 6 : 34);
    }

    internal IEnumerable<FirehoseStorageRange> Split(uint physicalPartitionNumber, FirehoseStorageRange range)
    {
        long end = checked(range.StartSector + range.SectorCount);
        if (physicalPartitionNumber != 0 || ProtectedSector is not { } sector ||
            !string.Equals(range.Label, "PrimaryGPT", StringComparison.OrdinalIgnoreCase) ||
            sector < range.StartSector || sector >= end)
        {
            yield return range;
            yield break;
        }
        if (range.StartSector < sector)
            yield return range with { SectorCount = sector - range.StartSector };
        yield return range with { StartSector = sector, SectorCount = 1 };
        if (sector + 1 < end)
            yield return range with { StartSector = sector + 1, SectorCount = end - sector - 1 };
    }

    private static bool TryRead(FirehoseSession session, uint sectorSize, int bufferSize, long start, long count,
        string label, string fileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            session.Execute(new ReadCommand
            {
                PhysicalPartitionNumber = 0,
                SectorSizeInBytes = sectorSize,
                StartSector = start.ToString(CultureInfo.InvariantCulture),
                NumPartitionSectors = count.ToString(CultureInfo.InvariantCulture),
                Label = label,
                FileName = fileName
            }, expectedRawMode: true);
        }
        catch (FirehoseNakException)
        {
            return false;
        }
        // Once raw mode begins, transfer failures must propagate rather than trigger another probe.
        session.ReceiveRaw(Stream.Null, checked(count * sectorSize), bufferSize, cancellationToken: cancellationToken);
        return true;
    }
}
