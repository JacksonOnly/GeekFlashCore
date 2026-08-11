using System.Buffers.Binary;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Ext.Constants;
using GeekFlashCore.FileSystem.Ext.Internals;
using GeekFlashCore.FileSystem.Ext.Models;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;

namespace GeekFlashCore.FileSystem.Ext;

public sealed class ExtFileSystemDriver : IFileSystemDriver
{
    public string FormatId => ExtFormat.FormatId;

    public FileSystemProbeResult Probe(
        IReadableBlockDevice source,
        FileSystemReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = limits ?? FileSystemReadLimits.Default;
        Span<byte> raw = stackalloc byte[ExtFormat.SuperblockSize];
        if (!ExtSuperblockReader.TryReadRaw(source, raw))
            return FileSystemProbeResult.NotRecognized;
        if (BinaryPrimitives.ReadUInt16LittleEndian(raw[56..]) != ExtFormat.Magic)
            return FileSystemProbeResult.NotRecognized;

        try
        {
            ExtSuperblock superblock = ExtSuperblockReader.Parse(source, raw);
            ulong[] unsupported = ExtSuperblockReader.GetUnsupportedFeatures(superblock);
            FileSystemProbeStatus status = unsupported.Length == 0
                ? FileSystemProbeStatus.RecognizedSupported
                : FileSystemProbeStatus.RecognizedUnsupported;
            return new FileSystemProbeResult(
                status,
                ExtFormat.FormatId,
                ExtFormat.ResourceKey,
                100,
                superblock.DeclaredLength,
                unsupported);
        }
        catch (ExtFileSystemException)
        {
            return new FileSystemProbeResult(
                FileSystemProbeStatus.RecognizedCorrupt,
                ExtFormat.FormatId,
                ExtFormat.ResourceKey,
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
            Span<byte> raw = stackalloc byte[ExtFormat.SuperblockSize];
            if (!ExtSuperblockReader.TryReadRaw(source, raw) ||
                BinaryPrimitives.ReadUInt16LittleEndian(raw[56..]) != ExtFormat.Magic)
            {
                throw new ExtFileSystemException(Strings.InvalidFormat);
            }

            ExtSuperblock superblock = ExtSuperblockReader.Parse(source, raw);
            ulong[] unsupported = ExtSuperblockReader.GetUnsupportedFeatures(superblock);
            if (unsupported.Length != 0)
            {
                throw new ExtFileSystemException(Strings.UnsupportedFeature);
            }

            ExtVolume volume = ExtVolume.Create(
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
