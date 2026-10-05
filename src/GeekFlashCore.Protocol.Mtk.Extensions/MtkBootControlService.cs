using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Standard A/B slot operations with minimal sector writes, mandatory backup and readback.</summary>
public sealed class MtkBootControlService
{
    private readonly IMtkSessionAccess _access;
    public MtkBootControlService(IMtkProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        _access = protocol as IMtkSessionAccess ?? throw new MtkCapabilityException("scoped DA channel");
    }
    private static (MtkFlashRange Window, int Prefix) Window(IMtkDaChannel c, MtkFlashRange partition)
    {
        var region = c.Storage.Regions.SingleOrDefault(r => r.WireId == partition.RegionId) ?? throw new MtkCapabilityException("boot control region");
        if (region.Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs or MtkStorageKind.Sdmmc) ||
            partition.Offset < 0 || partition.Offset % region.BlockSize != 0 ||
            partition.Length < MtkBootControlCodec.MetadataOffset + MtkBootControlCodec.MetadataSize ||
            partition.Offset > region.Length - partition.Length)
            throw new ArgumentOutOfRangeException(nameof(partition));
        int prefix = MtkBootControlCodec.MetadataOffset % region.BlockSize;
        long offset = checked(partition.Offset + MtkBootControlCodec.MetadataOffset - prefix);
        int length = checked((prefix + MtkBootControlCodec.MetadataSize + region.BlockSize - 1) / region.BlockSize * region.BlockSize);
        if (offset + length > partition.Offset + partition.Length) throw new ArgumentOutOfRangeException(nameof(partition));
        return (new(partition.RegionId, offset, length), prefix);
    }
    /// <summary>Reads the boot-control record from an explicitly resolved misc/para range.</summary>
    public MtkBootControlInfo Read(MtkFlashRange partition, CancellationToken cancellationToken = default) => _access.UseSession(c =>
    {
        var (window, prefix) = Window(c, partition); byte[] bytes = new byte[(int)window.Length];
        try
        {
            using var output = new MemoryStream(bytes, true); c.ReadFlash(window, output);
            return MtkBootControlCodec.Parse(bytes.AsSpan(prefix, MtkBootControlCodec.MetadataSize));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }, cancellationToken);
    /// <summary>Backs up the complete original write window, changes the slot and validates a full readback.
    /// The caller must durably persist the borrowed backup stream; unknown writes are never retried.</summary>
    public void SetActiveSlot(MtkFlashRange partition, int slot, Stream backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (!backup.CanWrite) throw new ArgumentException(nameof(backup));
        if (slot is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(slot));
        _access.UseSession(c =>
        {
            var (window, prefix) = Window(c, partition);
            byte[] original = new byte[(int)window.Length], current = new byte[(int)window.Length];
            byte[]? replacement = null, metadata = null; bool writing = false;
            try
            {
                using (var output = new MemoryStream(original, true)) c.ReadFlash(window, output);
                metadata = MtkBootControlCodec.SetActiveSlot(original.AsSpan(prefix, MtkBootControlCodec.MetadataSize), slot);
                replacement = original.ToArray(); metadata.CopyTo(replacement, prefix);
                backup.Write(original);
                if (backup is FileStream file) file.Flush(true); else backup.Flush();
                if (original.AsSpan().SequenceEqual(replacement)) return 0;
                using (var source = new MemoryStream(replacement, false)) { writing = true; c.WriteFlash(window, source); }
                using (var output = new MemoryStream(current, true)) c.ReadFlash(window, output);
                if (!CryptographicOperations.FixedTimeEquals(current, replacement) ||
                    MtkBootControlCodec.Parse(current.AsSpan(prefix, MtkBootControlCodec.MetadataSize)).ActiveSlot != slot)
                    throw new MtkResourceException("boot control readback");
                return 0;
            }
            catch (Exception ex) when (writing) { c.Invalidate(); throw new MtkBootControlWriteException(ex); }
            finally
            {
                CryptographicOperations.ZeroMemory(original); CryptographicOperations.ZeroMemory(current);
                if (replacement is not null) CryptographicOperations.ZeroMemory(replacement);
                if (metadata is not null) CryptographicOperations.ZeroMemory(metadata);
            }
        }, cancellationToken);
    }
}
