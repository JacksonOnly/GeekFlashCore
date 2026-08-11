using GeekFlashCore.FileSystem.Ext.Types;

namespace GeekFlashCore.FileSystem.Ext.Models;

public sealed class ExtSuperblock
{
    private readonly byte[] _uuidBytes;

    internal ExtSuperblock(
        uint inodeCount,
        ulong blockCount,
        uint firstDataBlock,
        int blockSize,
        uint blocksPerGroup,
        uint inodesPerGroup,
        ushort inodeSize,
        ushort descriptorSize,
        ExtCompatibleFeatures compatibleFeatures,
        ExtIncompatibleFeatures incompatibleFeatures,
        ExtReadOnlyCompatibleFeatures readOnlyCompatibleFeatures,
        byte[] uuidBytes,
        string? label,
        ushort state,
        uint checksumSeed)
    {
        InodeCount = inodeCount;
        BlockCount = blockCount;
        FirstDataBlock = firstDataBlock;
        BlockSize = blockSize;
        BlocksPerGroup = blocksPerGroup;
        InodesPerGroup = inodesPerGroup;
        InodeSize = inodeSize;
        DescriptorSize = descriptorSize;
        CompatibleFeatures = compatibleFeatures;
        IncompatibleFeatures = incompatibleFeatures;
        ReadOnlyCompatibleFeatures = readOnlyCompatibleFeatures;
        _uuidBytes = uuidBytes;
        Uuid = new Guid(uuidBytes);
        Label = label;
        State = state;
        ChecksumSeed = checksumSeed;
        DeclaredLength = checked((long)blockCount * blockSize);
        GroupCount = checked((uint)((blockCount - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup));
    }

    public uint InodeCount { get; }
    public ulong BlockCount { get; }
    public uint FirstDataBlock { get; }
    public int BlockSize { get; }
    public uint BlocksPerGroup { get; }
    public uint InodesPerGroup { get; }
    public ushort InodeSize { get; }
    public ushort DescriptorSize { get; }
    public ExtCompatibleFeatures CompatibleFeatures { get; }
    public ExtIncompatibleFeatures IncompatibleFeatures { get; }
    public ExtReadOnlyCompatibleFeatures ReadOnlyCompatibleFeatures { get; }
    public Guid Uuid { get; }
    public string? Label { get; }
    public ushort State { get; }
    public uint ChecksumSeed { get; }
    public long DeclaredLength { get; }
    public uint GroupCount { get; }
    public ReadOnlyMemory<byte> UuidBytes => _uuidBytes;
}