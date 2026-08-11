namespace GeekFlashCore.FileSystem.Abstractions;

[Flags]
public enum FileSystemNodeCapabilities
{
    None = 0,
    Read = 1 << 0,
    Enumerate = 1 << 1,
    ReadSymbolicLink = 1 << 2,
    ReadExtendedAttributes = 1 << 3,
    NativeDetails = 1 << 4
}