namespace GeekFlashCore.FileSystem.Abstractions;

public enum FileSystemNodeType
{
    Unknown,
    RegularFile,
    Directory,
    SymbolicLink,
    CharacterDevice,
    BlockDevice,
    Fifo,
    Socket
}