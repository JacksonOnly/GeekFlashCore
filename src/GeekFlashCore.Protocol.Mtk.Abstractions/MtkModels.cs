using System.Security.Cryptography;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>The independently implemented download-agent dialect.</summary>
public enum MtkDaKind
{
    Legacy, XFlash, Xml
}
/// <summary>The observed boot stage; USB PID alone does not prove a stage.</summary>
public enum MtkBootStage
{
    Unknown, Brom, Preloader, Da1, Da2
}
/// <summary>The serialized session lifecycle.</summary>
public enum MtkSessionState
{
    Disconnected, Opening, Handshaking, Probed, Authenticating, UploadingDa1, Da1Ready, InitializingEmi, UploadingDa2, Da2Ready, Extending, StorageReady, Faulted, Disposed
}
/// <summary>Storage command identifiers, independent of region identifiers.</summary>
public enum MtkStorageKind : uint
{
    Emmc = 1, Sdmmc = 2, Nand = 0x10, Nor = 0x20, Ufs = 0x30
}
/// <summary>Evidence-backed capability status.</summary>
public enum MtkCapabilitySupport
{
    Unknown, Unsupported, Supported, RequiresExtension
}
/// <summary>Authentication purpose; signatures are supplied by the host.</summary>
public enum MtkAuthenticationKind
{
    BromSla, DaSla,
    /// <summary>XML DA1 SLA, completed before the DA1 host checkpoint. DaSla retains its DA2 purpose.</summary>
    Da1Sla
}
/// <summary>Observed standard DA authentication phase. Unsupported is not authentication success.</summary>
public enum MtkDaAuthenticationState
{
    NotQueried, NotRequired, Authenticated, Unsupported
}
/// <summary>Hardware security flags exactly as reported by the target.</summary>
public sealed record MtkSecurityConfiguration(uint Raw)
{
    public bool SecureBoot => (Raw & 1) != 0;
    public bool Sla => (Raw & 2) != 0;
    public bool Daa => (Raw & 4) != 0;
    public bool CertificateRequired => (Raw & 0x10) != 0;
    public bool EmmcBootParameterPresent => (Raw & 8) != 0;
    public bool MemoryReadAuthenticationRequired => (Raw & 0x20) != 0;
    public bool MemoryWriteAuthenticationRequired => (Raw & 0x40) != 0;
    public bool CacheCommandBlocked => (Raw & 0x80) != 0;
}
/// <summary>A non-sensitive immutable probe snapshot. No identity is inferred from zero values.</summary>
public sealed record MtkTargetInfo(ushort HardwareCode, ushort HardwareSubCode, ushort HardwareVersion,
    ushort SoftwareVersion, byte BromVersion, byte PreloaderVersion, MtkBootStage Stage, MtkSecurityConfiguration Security)
{
    /// <summary>Initial FD version, independent of the later FC hardware version.</summary>
    public ushort InitialHardwareVersion { get; init; }
    /// <summary>Reference CPU name when the hardware code is known.</summary>
    public string? ChipName { get; init; }
    /// <summary>Reference CPU family description, when available.</summary>
    public string? ChipDescription { get; init; }
    /// <summary>Resolved DA hardware code; it can differ from the BROM hardware code.</summary>
    public ushort? DaHardwareCode { get; init; }
    /// <summary>Observed result of standard watchdog preparation.</summary>
    public MtkWatchdogState WatchdogState { get; init; }
}
/// <summary>Watchdog preparation is only successful after the register write is acknowledged.</summary>
public enum MtkWatchdogState
{
    NotRequested, ProfileUnavailable, Disabled
}
/// <summary>A validated ordinary storage region. RPMB is deliberately excluded.</summary>
public sealed record MtkStorageRegion
{
    public MtkStorageRegion(MtkStorageKind kind, uint wireId, string name, ulong length, int blockSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs or MtkStorageKind.Sdmmc or MtkStorageKind.Nor or MtkStorageKind.Nand) ||
            kind == MtkStorageKind.Emmc && (wireId is < 1 or > 8 || wireId == 3) ||
            kind == MtkStorageKind.Ufs && wireId is < 1 or > 3 ||
            kind is MtkStorageKind.Sdmmc or MtkStorageKind.Nor or MtkStorageKind.Nand && wireId != 8 ||
            length == 0 || length > long.MaxValue || blockSize < (kind == MtkStorageKind.Nor ? 1 : 512) || blockSize > 65536 ||
            (blockSize & (blockSize - 1)) != 0 || length % (uint)blockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        Kind = kind;
        WireId = wireId;
        Name = name;
        Length = (long)length;
        BlockSize = blockSize;
        EraseBlockSize = kind == MtkStorageKind.Nor ? 0 : blockSize;
    }
    public MtkStorageKind Kind
    {
        get;
    }
    public uint WireId
    {
        get;
    }
    public string Name
    {
        get;
    }
    public long Length
    {
        get;
    }
    public int BlockSize
    {
        get;
    }
    /// <summary>Confirmed erase alignment; zero means erase geometry has not been supplied.</summary>
    public int EraseBlockSize { get; init; }
    /// <summary>Whether ordinary writes are enabled for this region's data semantics.</summary>
    public bool CanWrite { get; init; } = true;
}
/// <summary>Ordinary regions and separately reported RPMB capacity in 256-byte data blocks.</summary>
public sealed record MtkStorageInfo(MtkStorageKind Kind, IReadOnlyList<MtkStorageRegion> Regions, uint UserRegionId, uint RpmbDataBlocks)
{
    /// <summary>Reported NAND geometry; logical flash ranges contain data pages only, excluding spare/OOB.</summary>
    public MtkNandGeometry? Nand { get; init; }
}
/// <summary>NAND device geometry, separate from logical data-page ranges and erase alignment.</summary>
public sealed record MtkNandGeometry(uint Type, int PageSize, int SpareSize, int EraseBlockSize,
    ulong TotalSize, ulong AvailableSize, bool HasBadBlockTable)
{
    /// <summary>False when XML reports only total size and does not confirm usable logical capacity or BMT.</summary>
    public bool LogicalCapacityConfirmed { get; init; } = true;
}
/// <summary>A typed byte range. Offset and length must be aligned to the region's logical blocks.</summary>
public readonly record struct MtkFlashRange(uint RegionId, long Offset, long Length);
/// <summary>A region window in a reopenable DA container. EntryOffset retains m_start_offset metadata;
/// it is a region length/signature boundary, never a displacement added to Address for execution.</summary>
public sealed record MtkDaRegion(long FileOffset, uint Length, uint Address, uint EntryOffset, uint SignatureLength);
/// <summary>A DA metadata entry. Region index is a zero-based table index.</summary>
public sealed record MtkDaEntry(ushort HardwareCode, ushort HardwareSubCode, ushort HardwareVersion,
    ushort SoftwareVersion, ushort EntryRegionIndex, MtkDaKind Kind, IReadOnlyList<MtkDaRegion> Regions)
{
    /// <summary>Unmodified container index, when parsed. EntryRegionIndex identifies the actual first stage.</summary>
    public ushort? RawEntryRegionIndex { get; init; }
}
/// <summary>An explicitly selected image; the host retains ownership of the data source.</summary>
public sealed record MtkDaImage(IDataSource Source, MtkDaEntry Entry);
/// <summary>Borrowed EMI data extracted and validated by the parser or host.
/// Source preserves the Legacy MTK_BIN body. Hosts supplying only Source retain their explicit wire data.</summary>
public sealed record MtkEmiImage(IDataSource Source, uint Version)
{
    /// <summary>Optional complete MTK_BLOADER_INFO window required by XFlash, including its header.
    /// Borrowed like Source; the protocol disposes only streams that it opens, not either data source.</summary>
    public IDataSource? BloaderInfoSource { get; init; }
}
/// <summary>Capabilities are immutable snapshots and never advertise exploit support.</summary>
public sealed record MtkCapabilities(MtkCapabilitySupport Flash, MtkCapabilitySupport Memory,
    MtkCapabilitySupport Crypto, MtkCapabilitySupport Rpmb, MtkCapabilitySupport SecurityConfiguration);

/// <summary>An owned sensitive buffer. Construction transfers the array; disposal clears it.</summary>
public sealed class MtkSensitiveBuffer(byte[] buffer) : IDisposable
{
    private byte[]? _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    public ReadOnlyMemory<byte> Memory => _buffer ?? throw new ObjectDisposedException(nameof(MtkSensitiveBuffer));
    public void Dispose()
    {
        byte[]? value = Interlocked.Exchange(ref _buffer, null);
        if (value is not null)
            CryptographicOperations.ZeroMemory(value);
    }
}
