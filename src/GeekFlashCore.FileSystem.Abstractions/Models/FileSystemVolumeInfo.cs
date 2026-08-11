namespace GeekFlashCore.FileSystem.Abstractions;

public sealed record FileSystemVolumeInfo(
    string FormatId,
    string ResourceKey,
    string? Label,
    Guid? Uuid,
    long DeclaredLength,
    int BlockSize,
    bool IntegrityVerified);
