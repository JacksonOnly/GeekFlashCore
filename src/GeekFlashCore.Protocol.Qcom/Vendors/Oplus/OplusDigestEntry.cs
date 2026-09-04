namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

public sealed record OplusDigestEntry(
    uint PhysicalPartitionNumber,
    string Label,
    string FileName,
    bool AllowRead,
    bool AllowWrite,
    long StartSector,
    long SectorCount,
    string HashHex)
{
    public long EndSectorExclusive => checked(StartSector + SectorCount);
}
