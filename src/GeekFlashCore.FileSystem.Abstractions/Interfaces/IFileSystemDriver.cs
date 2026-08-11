using GeekFlashCore.IO.BlockDevice.Abstractions;

namespace GeekFlashCore.FileSystem.Abstractions;

public interface IFileSystemDriver
{
    string FormatId { get; }

    FileSystemProbeResult Probe(
        IReadableBlockDevice source,
        FileSystemReadLimits? limits = null);

    IFileSystemVolume Open(
        IReadableBlockDevice source,
        DeviceOwnership ownership,
        FileSystemReadLimits? limits = null);
}