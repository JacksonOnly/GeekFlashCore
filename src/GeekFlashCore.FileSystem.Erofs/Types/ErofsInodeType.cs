namespace GeekFlashCore.FileSystem.Erofs.Types;

public enum ErofsInodeType
{
    Unknown,
    Fifo,
    CharacterDevice,
    Directory,
    BlockDevice,
    RegularFile,
    SymbolicLink,
    Socket
}