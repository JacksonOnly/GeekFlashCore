using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

public sealed class OplusDigestIndex
{
    private readonly OplusDigestEntry[] _entries;

    public OplusDigestIndex(IEnumerable<OplusDigestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.OrderBy(static entry => entry.PhysicalPartitionNumber)
            .ThenBy(static entry => entry.StartSector).ToArray();
        for (int index = 0; index < _entries.Length; index++)
        {
            OplusDigestEntry entry = _entries[index];
            if (string.IsNullOrWhiteSpace(entry.Label) || string.IsNullOrWhiteSpace(entry.FileName) ||
                entry.StartSector < 0 || entry.SectorCount <= 0)
                throw new OplusDigestException("The Oplus Digest contains an invalid partition entry.");
            _ = entry.EndSectorExclusive;
            if (index > 0)
            {
                OplusDigestEntry previous = _entries[index - 1];
                if (previous.PhysicalPartitionNumber == entry.PhysicalPartitionNumber &&
                    previous.EndSectorExclusive > entry.StartSector)
                    throw new OplusDigestException("The Oplus Digest contains overlapping partition entries.");
            }
        }
    }

    public IReadOnlyList<OplusDigestEntry> Entries => _entries;

    internal OplusDigestEntry? Find(
        uint physicalPartitionNumber,
        long sector,
        string? label,
        string? fileName)
    {
        foreach (OplusDigestEntry entry in _entries)
        {
            if (entry.PhysicalPartitionNumber == physicalPartitionNumber && sector >= entry.StartSector &&
                sector < entry.EndSectorExclusive && Matches(entry.Label, label) && Matches(entry.FileName, fileName))
                return entry;
        }
        return null;
    }

    private static bool Matches(string actual, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
