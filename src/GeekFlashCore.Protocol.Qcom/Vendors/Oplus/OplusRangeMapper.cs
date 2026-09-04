using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

public sealed record OplusMappedRange(OplusDigestEntry Entry, long StartSector, long SectorCount);

public static class OplusRangeMapper
{
    public static IReadOnlyList<OplusMappedRange> Map(
        OplusDigestIndex index,
        uint physicalPartitionNumber,
        long startSector,
        long sectorCount,
        bool write,
        string? label = null,
        string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (startSector < 0 || sectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sectorCount));
        long end = checked(startSector + sectorCount);
        var ranges = new List<OplusMappedRange>();
        long current = startSector;
        while (current < end)
        {
            OplusDigestEntry entry = index.Find(physicalPartitionNumber, current, label, fileName) ??
                                     throw new OplusDigestException("The requested range is not mapped by the verified Oplus Digest.");
            if (write ? !entry.AllowWrite : !entry.AllowRead)
                throw new OplusDigestException("The verified Oplus Digest does not permit the requested operation.");
            long count = Math.Min(end, entry.EndSectorExclusive) - current;
            ranges.Add(new OplusMappedRange(entry, current, count));
            current = checked(current + count);
        }
        return ranges;
    }
}
