using GeekFlashCore.Protocol.Qcom.Firehose.Storage;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

/// <summary>Uses an index already accepted by the connected device.</summary>
public sealed class OplusDigestPtPolicy : IFirehoseStoragePolicy
{
    private readonly OplusDigestIndex _index;
    private readonly OplusGptCompatibility? _compatibility;

    public OplusDigestPtPolicy(OplusDigestIndex index, OplusGptCompatibility? compatibility = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _compatibility = compatibility;
    }

    public IReadOnlyList<FirehoseStorageRange> Map(uint physicalPartitionNumber, long startSector, long sectorCount,
        bool write, string? label = null, string? fileName = null)
    {
        IReadOnlyList<OplusMappedRange> mapped = OplusRangeMapper.Map(
            _index, physicalPartitionNumber, startSector, sectorCount, write, label, fileName);
        var ranges = new List<FirehoseStorageRange>();
        foreach (OplusMappedRange range in mapped)
        {
            var value = new FirehoseStorageRange(range.StartSector, range.SectorCount, range.Entry.Label, range.Entry.FileName);
            if (_compatibility is null) ranges.Add(value);
            else ranges.AddRange(_compatibility.Split(physicalPartitionNumber, value));
        }
        return ranges;
    }
}
