using GeekFlashCore.FileSystem.Erofs.Types;

namespace GeekFlashCore.FileSystem.Erofs.Models;

public sealed class ErofsSuperblock
{
    private readonly byte[] _uuidBytes;

    internal ErofsSuperblock(
        ErofsCompatibleFeatures compatibleFeatures,
        ErofsIncompatibleFeatures incompatibleFeatures,
        int blockSize,
        byte extensionSlots,
        ulong rootNodeId,
        ulong inodeCount,
        long epoch,
        uint fixedNanoseconds,
        ulong blockCount,
        ulong metadataBlock,
        ulong xattrBlock,
        byte[] uuidBytes,
        string? volumeName,
        ushort availableCompressionAlgorithms,
        ushort extraDevices,
        ushort deviceTableSlotOffset,
        byte xattrPrefixCount,
        uint xattrPrefixStart,
        ulong packedNodeId,
        byte ishareXattrPrefixId,
        ulong metaboxNodeId,
        uint storedChecksum)
    {
        CompatibleFeatures = compatibleFeatures;
        IncompatibleFeatures = incompatibleFeatures;
        BlockSize = blockSize;
        BlockSizeBits = checked((byte)System.Numerics.BitOperations.Log2((uint)blockSize));
        ExtensionSlots = extensionSlots;
        RootNodeId = rootNodeId;
        InodeCount = inodeCount;
        Epoch = epoch;
        FixedNanoseconds = fixedNanoseconds;
        BlockCount = blockCount;
        MetadataBlock = metadataBlock;
        XattrBlock = xattrBlock;
        _uuidBytes = uuidBytes;
        Uuid = new Guid(uuidBytes, bigEndian: true);
        VolumeName = volumeName;
        AvailableCompressionAlgorithms = availableCompressionAlgorithms;
        ExtraDevices = extraDevices;
        DeviceTableSlotOffset = deviceTableSlotOffset;
        XattrPrefixCount = xattrPrefixCount;
        XattrPrefixStart = xattrPrefixStart;
        PackedNodeId = packedNodeId;
        IshareXattrPrefixId = ishareXattrPrefixId;
        MetaboxNodeId = metaboxNodeId;
        StoredChecksum = storedChecksum;
        DeclaredLength = checked((long)blockCount * blockSize);
    }

    public ErofsCompatibleFeatures CompatibleFeatures { get; }
    public ErofsIncompatibleFeatures IncompatibleFeatures { get; }
    public int BlockSize { get; }
    public byte BlockSizeBits { get; }
    public byte ExtensionSlots { get; }
    public ulong RootNodeId { get; }
    public ulong InodeCount { get; }
    public long Epoch { get; }
    public uint FixedNanoseconds { get; }
    public ulong BlockCount { get; }
    public ulong MetadataBlock { get; }
    public ulong XattrBlock { get; }
    public Guid Uuid { get; }
    public ReadOnlyMemory<byte> UuidBytes => _uuidBytes;
    public string? VolumeName { get; }
    public ushort AvailableCompressionAlgorithms { get; }
    public ushort ExtraDevices { get; }
    public ushort DeviceTableSlotOffset { get; }
    public byte XattrPrefixCount { get; }
    public uint XattrPrefixStart { get; }
    public ulong PackedNodeId { get; }
    public byte IshareXattrPrefixId { get; }
    public ulong MetaboxNodeId { get; }
    public uint StoredChecksum { get; }
    public long DeclaredLength { get; }
}