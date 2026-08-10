namespace GeekFlashCore.Gpt.Abstractions;

public interface IGpt
{
    GptImageType ImageType { get; }
    GptContainerType ContainerType { get; }
    int SectorSize { get; }
    ulong TotalDiskSectors { get; }
    int AvailableEntryCount { get; }
    GptHeader Header { get; }
    IReadOnlyList<GptEntry> Entries { get; }
    IReadOnlyList<GptUnallocatedRegion> UnallocatedRegions { get; }
    IReadOnlyList<GptPartitionOverlap> Overlaps { get; }
    GptCrcStatus CrcStatus { get; }

    GptRedundancyStatus SourceRedundancyStatus { get; }
    GptRedundancyStatus RedundancyStatus { get; }

    GptCrcStatus ValidateCrc();
    void RepairCrc();
    void InsertPartition(int position, GptEntry partition);
    void CreatePartition(GptEntry partition);
    void UpdatePartition(int number, GptEntry partition);
    void DeletePartition(int number);
    void MovePartition(int number, int newPosition);
    void MovePartitionToSlot(int number, int slotIndex);
    void MovePartitionGeometry(int number, ulong firstLba);
    void ShiftPartition(int number, long sectorOffset);
    void SetPartitionEntryCount(int entryCount);
    byte[] ExportPartition(int number);
    void Patch(ulong? lastUsableLba = null);
    void Unpatch(ulong? lastUsableLba = null);
    void WriteTo(Stream destination, GptExportOptions? options = null);
    byte[] ToArray(GptExportOptions? options = null);
    void SaveFile(string path, GptExportOptions? options = null);
}
