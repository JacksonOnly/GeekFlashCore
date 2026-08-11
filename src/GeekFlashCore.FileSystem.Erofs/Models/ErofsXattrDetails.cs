namespace GeekFlashCore.FileSystem.Erofs.Models;

public readonly record struct ErofsXattrDetails(
    byte NameIndex,
    ushort ValueSize,
    long EntryOffset,
    bool Shared);
