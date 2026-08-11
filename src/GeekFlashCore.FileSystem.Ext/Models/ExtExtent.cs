namespace GeekFlashCore.FileSystem.Ext.Models;

public readonly record struct ExtExtent(
    uint LogicalBlock,
    uint BlockCount,
    ulong PhysicalBlock,
    bool Unwritten);