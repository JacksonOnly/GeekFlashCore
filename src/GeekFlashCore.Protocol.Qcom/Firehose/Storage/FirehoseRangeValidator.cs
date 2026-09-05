using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Storage;

public static class FirehoseRangeValidator
{
    public static long GetByteLength(long sectorCount, uint sectorSizeInBytes)
    {
        if (sectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sectorCount));
        if (sectorSizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(sectorSizeInBytes));
        return checked(sectorCount * sectorSizeInBytes);
    }

    public static void ValidateSectorRange(
        uint physicalPartitionNumber,
        long startSector,
        long sectorCount,
        uint sectorSizeInBytes)
    {
        if (physicalPartitionNumber >= FirehoseConstants.MaximumPhysicalPartitionCount)
            throw new ArgumentOutOfRangeException(nameof(physicalPartitionNumber));
        if (startSector < 0)
            throw new ArgumentOutOfRangeException(nameof(startSector));
        _ = GetByteLength(sectorCount, sectorSizeInBytes);
        long endSector = checked(startSector + sectorCount);
        _ = checked(endSector * sectorSizeInBytes);
    }

    public static void ValidateByteRange(
        long offset,
        int length,
        int logicalBlockSize,
        long deviceLength)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (logicalBlockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalBlockSize));
        if (deviceLength < 0)
            throw new ArgumentOutOfRangeException(nameof(deviceLength));
        if (offset % logicalBlockSize != 0 || length % logicalBlockSize != 0)
            throw new ArgumentException(Strings.Qcom_ByteRangeUnaligned);
        long end = checked(offset + length);
        if (end > deviceLength)
            throw new ArgumentOutOfRangeException(nameof(length), Strings.Qcom_ByteRangeExceedsDevice);
    }
}
