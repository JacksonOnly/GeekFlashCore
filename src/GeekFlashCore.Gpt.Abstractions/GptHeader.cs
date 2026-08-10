namespace GeekFlashCore.Gpt.Abstractions;

public sealed record GptHeader(
    uint Revision,
    uint HeaderSize,
    uint HeaderCrc32,
    uint Reserved,
    ulong CurrentLba,
    ulong AlternateLba,
    ulong FirstUsableLba,
    ulong LastUsableLba,
    Guid DiskId,
    ulong PartitionEntryLba,
    uint PartitionEntryCount,
    uint PartitionEntrySize,
    uint PartitionEntryArrayCrc32);
