using GeekFlashCore.Android.Sparse;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.Android.Sparse.Types;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

internal static class SparseProgramPlanner
{
    public static IReadOnlyList<FirehoseProgramSegment> Create(
        Stream source,
        FirehoseProgramRequest request,
        CancellationToken cancellationToken = default)
    {
        long position = source.Position;
        using var sourceDevice = new StreamBlockDevice(
            source,
            checked(source.Length - position),
            DeviceOwnership.Borrow,
            logicalBlockSize: 1,
            id: new BlockDeviceId("qcom-sparse-source"));
        using var document = SparseImageParser.Open(sourceDevice, DeviceOwnership.Borrow);
        if (document.ExpandedLength > request.GetWireLength())
            throw new ArgumentException(Strings.Qcom_SparseRegionExceedsTarget, nameof(request));
        if (document.ChecksumStatus == SparseChecksumStatus.NotVerified)
            document.VerifyChecksum(cancellationToken: cancellationToken);

        uint sectorSize = request.SectorSizeInBytes;
        if (document.Header.BlockSize % sectorSize != 0)
        {
            throw new ArgumentException(
                Strings.Qcom_SparseBlockSizeUnaligned,
                nameof(request));
        }

        long sectorsPerBlock = document.Header.BlockSize / sectorSize;
        IReadOnlyList<SparseRegion> regions = document.CreateDataRegions();
        var segments = new FirehoseProgramSegment[regions.Count];
        for (int index = 0; index < segments.Length; index++)
        {
            SparseRegion region = regions[index];
            if (region.Length <= 0 || region.Length % sectorSize != 0)
                throw new InvalidDataException(Strings.Qcom_SparseRegionUnaligned);

            long sectorOffset = checked((long)region.StartBlock * sectorsPerBlock);
            long startSector = checked(request.StartSector + sectorOffset);
            long sectorCount = region.Length / sectorSize;
            if (sectorOffset > request.SectorCount - sectorCount)
                throw new ArgumentException(Strings.Qcom_SparseRegionExceedsTarget, nameof(request));
            segments[index] = FirehoseProgramSegment.Sparse(startSector, sectorCount, region);
        }
        return segments;
    }
}
