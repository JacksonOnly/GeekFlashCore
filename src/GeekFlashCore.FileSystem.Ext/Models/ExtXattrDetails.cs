namespace GeekFlashCore.FileSystem.Ext.Models;

public readonly record struct ExtXattrDetails(
    byte NameIndex,
    ushort ValueOffset,
    uint ValueInode,
    uint ValueSize,
    uint Hash,
    long EntryOffset);
