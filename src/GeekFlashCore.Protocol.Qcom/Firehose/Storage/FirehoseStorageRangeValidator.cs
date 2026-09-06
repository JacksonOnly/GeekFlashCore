namespace GeekFlashCore.Protocol.Qcom.Firehose.Storage;

internal static class FirehoseStorageRangeValidator
{
    internal static IReadOnlyList<FirehoseStorageRange> Validate(
        IReadOnlyList<FirehoseStorageRange>? ranges,
        long requestedStartSector,
        long requestedSectorCount)
    {
        if (ranges is null || ranges.Count == 0)
            throw new InvalidOperationException(Strings.Qcom_StoragePolicyMappingInvalid);
        ArgumentOutOfRangeException.ThrowIfNegative(requestedStartSector);
        if (requestedSectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSectorCount));

        long expectedStart = requestedStartSector;
        long remaining = requestedSectorCount;
        foreach (FirehoseStorageRange? range in ranges)
        {
            if (range is null ||
                range.SectorCount <= 0 ||
                range.StartSector != expectedStart ||
                range.SectorCount > remaining)
            {
                throw new InvalidOperationException(Strings.Qcom_StoragePolicyMappingInvalid);
            }

            expectedStart = checked(expectedStart + range.SectorCount);
            remaining -= range.SectorCount;
        }

        if (remaining != 0)
            throw new InvalidOperationException(Strings.Qcom_StoragePolicyMappingInvalid);

        return ranges;
    }
}
