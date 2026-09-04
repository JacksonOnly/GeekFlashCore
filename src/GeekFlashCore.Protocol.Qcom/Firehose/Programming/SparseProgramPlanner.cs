using GeekFlashCore.Android.Sparse;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

internal static class SparseProgramPlanner
{
    public static IReadOnlyList<FirehoseProgramSegment> Create(
        Stream source,
        FirehoseProgramRequest request)
    {
        SparseImage image = SparseImageParser.Parse(source);
        uint sectorSize = request.SectorSizeInBytes;
        if (image.Header.BlockSize % sectorSize != 0)
        {
            throw new ArgumentException(
                "The Android sparse block size must be a multiple of the Firehose sector size.",
                nameof(request));
        }

        long targetLength = request.GetWireLength();
        if (image.RawLength > targetLength)
        {
            throw new ArgumentException(
                "The expanded Android sparse image is larger than the target range.",
                nameof(request));
        }

        long sectorsPerBlock = image.Header.BlockSize / sectorSize;
        var segments = new FirehoseProgramSegment[image.Regions.Count];
        for (int index = 0; index < segments.Length; index++)
        {
            SparseRegion region = image.Regions[index];
            if (region.Length <= 0 || region.Length % sectorSize != 0)
                throw new InvalidDataException("An Android sparse region is not sector aligned.");

            long sectorOffset = checked((long)region.StartBlock * sectorsPerBlock);
            long startSector = checked(request.StartSector + sectorOffset);
            long sectorCount = region.Length / sectorSize;
            if (sectorOffset > request.SectorCount - sectorCount)
                throw new ArgumentException("An Android sparse region exceeds the target range.", nameof(request));
            segments[index] = FirehoseProgramSegment.Sparse(startSector, sectorCount, region);
        }
        return segments;
    }
}
