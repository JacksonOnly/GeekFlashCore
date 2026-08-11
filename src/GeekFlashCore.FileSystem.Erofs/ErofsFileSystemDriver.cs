using System.Buffers.Binary;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Erofs.Constants;
using GeekFlashCore.FileSystem.Erofs.Internals;
using GeekFlashCore.FileSystem.Erofs.Models;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;

namespace GeekFlashCore.FileSystem.Erofs;

public sealed class ErofsFileSystemDriver : IFileSystemDriver
{
    public string FormatId => ErofsFormat.FormatId;

    public FileSystemProbeResult Probe(IReadableBlockDevice source, FileSystemReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = limits ?? FileSystemReadLimits.Default;
        Span<byte> raw = stackalloc byte[ErofsFormat.SuperblockStructureSize];
        if (!ErofsSuperblockReader.TryReadRaw(source, raw)) return FileSystemProbeResult.NotRecognized;
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw) != ErofsFormat.Magic)
            return FileSystemProbeResult.NotRecognized;

        try
        {
            ErofsSuperblock superblock = ErofsSuperblockReader.Parse(source, raw);
            ErofsSuperblockReader.VerifyChecksum(source, superblock);
            ulong unsupported = ErofsSuperblockReader.GetUnsupportedFeature(superblock);
            return new FileSystemProbeResult(
                unsupported == 0
                    ? FileSystemProbeStatus.RecognizedSupported
                    : FileSystemProbeStatus.RecognizedUnsupported,
                ErofsFormat.FormatId,
                ErofsFormat.ResourceKey,
                100,
                superblock.DeclaredLength,
                unsupported == 0 ? default : new ulong[] { unsupported });
        }
        catch (ErofsFileSystemException)
        {
            return new FileSystemProbeResult(
                FileSystemProbeStatus.RecognizedCorrupt,
                ErofsFormat.FormatId,
                ErofsFormat.ResourceKey,
                100);
        }
    }

    public IFileSystemVolume Open(
        IReadableBlockDevice source,
        DeviceOwnership ownership,
        FileSystemReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(ownership)) throw new ArgumentOutOfRangeException(nameof(ownership));
        var lease = new BlockDeviceLease(source, ownership);
        try
        {
            Span<byte> raw = stackalloc byte[ErofsFormat.SuperblockStructureSize];
            if (!ErofsSuperblockReader.TryReadRaw(source, raw) ||
                BinaryPrimitives.ReadUInt32LittleEndian(raw) != ErofsFormat.Magic)
            {
                throw new ErofsFileSystemException(Strings.InvalidFormat);
            }

            ErofsSuperblock superblock = ErofsSuperblockReader.Parse(source, raw);
            ErofsSuperblockReader.VerifyChecksum(source, superblock);
            ulong unsupported = ErofsSuperblockReader.GetUnsupportedFeature(superblock);
            if (unsupported != 0)
            {
                throw new ErofsFileSystemException(Strings.UnsupportedFeature);
            }

            ErofsVolume volume = ErofsVolume.Create(
                lease,
                superblock,
                limits ?? FileSystemReadLimits.Default);
            lease = null!;
            return volume;
        }
        finally
        {
            lease?.Dispose();
        }
    }
}
