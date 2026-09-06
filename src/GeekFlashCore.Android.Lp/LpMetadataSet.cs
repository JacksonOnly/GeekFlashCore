using System.Buffers.Binary;
using System.Security.Cryptography;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.Android.Lp.Localization;
using GeekFlashCore.ImageFormats.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Lp;

public sealed class LpMetadataSet : IDisposable
{
    private readonly IReadableBlockDevice _source;
    private readonly bool _ownsSource;
    private readonly FormatLifetime _lifetime = new();
    private readonly ByteBudget _decodedBudget;

    private LpMetadataSet(
        IReadableBlockDevice source,
        DeviceOwnership ownership,
        ImageReadLimits limits,
        LpGeometry geometry,
        LpGeometryCopy primaryGeometry,
        LpGeometryCopy backupGeometry,
        LpMetadataSlotSummary[] slots)
    {
        _source = source;
        _ownsSource = ownership == DeviceOwnership.Transfer;
        _decodedBudget = new ByteBudget(limits.MaxDecodedLpMetadataBytes);
        Limits = limits;
        Geometry = geometry;
        PrimaryGeometry = primaryGeometry;
        BackupGeometry = backupGeometry;
        Slots = slots;
    }

    public ImageReadLimits Limits { get; }
    public LpGeometry Geometry { get; }
    public LpGeometryCopy PrimaryGeometry { get; }
    public LpGeometryCopy BackupGeometry { get; }
    public IReadOnlyList<LpMetadataSlotSummary> Slots { get; }

    public static LpMetadataSet Open(
        IReadableBlockDevice source,
        DeviceOwnership ownership,
        ImageReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(ownership)) throw new ArgumentOutOfRangeException(nameof(ownership));
        limits ??= ImageReadLimits.Default;
        limits.Validate();

        var validationBudget = new ByteBudget(
            Math.Max(limits.MaxDecodedLpMetadataBytes, limits.ChecksumBufferSize));
        var buffers = new BudgetedArrayPool(validationBudget);
        using PooledBufferLease checksumBuffer = buffers.Rent(limits.ChecksumBufferSize);

        LpGeometryCopy primary = ReadGeometryCopy(source, LpMetadataCopyKind.Primary);
        LpGeometryCopy backup = ReadGeometryCopy(source, LpMetadataCopyKind.Backup);
        LpGeometry geometry = primary.Geometry ?? backup.Geometry ??
            throw Failure(
                source,
                ImageFormatErrorCode.CorruptMetadata,
                Resources.Errors_Lp_BothGeometryCopiesInvalid,
                LpFormat.ReservedBytes,
                "geometry",
                "ImageFormats.Lp.InvalidGeometry");

        ValidateGeometryLimits(source, geometry, limits);
        long totalMetadataSize = checked(
            LpFormat.ReservedBytes +
            2L * LpFormat.GeometryBlockSize +
            2L * geometry.MetadataMaxSize * geometry.MetadataSlotCount);
        if (totalMetadataSize > source.Length)
        {
            throw Failure(
                source,
                ImageFormatErrorCode.IoFailure,
                Resources.Errors_Lp_MetadataCopiesExceedSource,
                totalMetadataSize,
                "geometry",
                "ImageFormats.Lp.Truncated",
                ImageFormatDiagnosticReason.Truncated);
        }

        var slots = new LpMetadataSlotSummary[geometry.MetadataSlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            LpMetadataCopySummary primaryCopy = ValidateMetadataCopy(
                source,
                geometry,
                slot,
                LpMetadataCopyKind.Primary,
                limits,
                checksumBuffer.Memory.Span);
            LpMetadataCopySummary backupCopy = ValidateMetadataCopy(
                source,
                geometry,
                slot,
                LpMetadataCopyKind.Backup,
                limits,
                checksumBuffer.Memory.Span);
            slots[slot] = new LpMetadataSlotSummary(slot, primaryCopy, backupCopy);
        }

        return new LpMetadataSet(source, ownership, limits, geometry, primary, backup, slots);
    }

    public LpMetadataDocument OpenPreferredSlot(int slotNumber)
    {
        ThrowIfDisposed();
        if ((uint)slotNumber >= (uint)Slots.Count)
            throw new ArgumentOutOfRangeException(nameof(slotNumber));
        LpMetadataCopySummary summary = Slots[slotNumber].Preferred ??
            throw Failure(
                _source,
                ImageFormatErrorCode.CorruptMetadata,
                Resources.FormatErrors_Lp_SlotHasNoValidCopy(slotNumber),
                GetMetadataOffset(Geometry, slotNumber, LpMetadataCopyKind.Primary),
                "metadata",
                "ImageFormats.Lp.InvalidSlot",
                objectId: $"slot:{slotNumber}");
        return OpenCopy(summary);
    }

    public LpMetadataDocument OpenCopy(int slotNumber, LpMetadataCopyKind kind)
    {
        ThrowIfDisposed();
        if ((uint)slotNumber >= (uint)Slots.Count)
            throw new ArgumentOutOfRangeException(nameof(slotNumber));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        LpMetadataCopySummary summary = kind == LpMetadataCopyKind.Primary
            ? Slots[slotNumber].Primary
            : Slots[slotNumber].Backup;
        if (summary.Status != LpCopyValidationStatus.Valid)
        {
            throw Failure(
                _source,
                ImageFormatErrorCode.CorruptMetadata,
                Resources.FormatErrors_Lp_MetadataCopyInvalid(slotNumber, kind),
                summary.Offset,
                "metadata",
                "ImageFormats.Lp.InvalidCopy",
                objectId: $"slot:{slotNumber}/{kind}");
        }

        return OpenCopy(summary);
    }

    public void Dispose()
    {
        if (_lifetime.IsDisposed) return;
        _lifetime.Dispose();
        if (_ownsSource) _source.Dispose();
        GC.SuppressFinalize(this);
    }

    internal IReadableBlockDevice Source
    {
        get
        {
            ThrowIfDisposed();
            return _source;
        }
    }

    internal void ThrowIfDisposed() => _lifetime.ThrowIfDisposed(this);

    private LpMetadataDocument OpenCopy(LpMetadataCopySummary summary)
    {
        LpMetadataHeader header = summary.Header!;
        long decodedBytes64 = checked(
            (long)header.Partitions.EntryCount * LpFormat.PartitionEntrySize +
            (long)header.Extents.EntryCount * LpFormat.ExtentEntrySize +
            (long)header.Groups.EntryCount * LpFormat.GroupEntrySize +
            (long)header.BlockDevices.EntryCount * LpFormat.BlockDeviceEntrySize);
        if (decodedBytes64 > int.MaxValue)
        {
            throw Failure(
                _source,
                ImageFormatErrorCode.ResourceLimitExceeded,
                Resources.Errors_Lp_DecodedReservationTooLarge,
                summary.Offset,
                "tables",
                "ImageFormats.Lp.DecodedLimit");
        }

        ByteBudgetLease reservation;
        try
        {
            reservation = _decodedBudget.Acquire(Math.Max(1, (int)decodedBytes64));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw Failure(
                _source,
                ImageFormatErrorCode.ResourceLimitExceeded,
                Resources.Errors_Lp_DecodedBudgetExceeded,
                summary.Offset,
                "tables",
                "ImageFormats.Lp.DecodedLimit",
                innerException: exception);
        }

        try
        {
            return LpMetadataDocument.Parse(this, summary, reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private static LpGeometryCopy ReadGeometryCopy(
        IReadableBlockDevice source,
        LpMetadataCopyKind kind)
    {
        long offset = kind == LpMetadataCopyKind.Primary
            ? LpFormat.ReservedBytes
            : LpFormat.ReservedBytes + LpFormat.GeometryBlockSize;
        try
        {
            Span<byte> block = stackalloc byte[LpFormat.GeometryBlockSize];
            BlockDeviceIO.ReadExactlyAt(source, offset, block);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(block);
            uint structSize = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            if (magic != LpFormat.GeometryMagic || structSize != LpFormat.GeometryStructSize)
            {
                return InvalidGeometry(source, kind, offset);
            }

            Span<byte> expected = stackalloc byte[32];
            block.Slice(8, 32).CopyTo(expected);
            block.Slice(8, 32).Clear();
            Span<byte> computed = stackalloc byte[32];
            SHA256.HashData(block[..LpFormat.GeometryStructSize], computed);
            if (!CryptographicOperations.FixedTimeEquals(expected, computed))
            {
                return InvalidGeometry(source, kind, offset);
            }

            var geometry = new LpGeometry(
                BinaryPrimitives.ReadUInt32LittleEndian(block[40..]),
                BinaryPrimitives.ReadUInt32LittleEndian(block[44..]),
                BinaryPrimitives.ReadUInt32LittleEndian(block[48..]));
            return new LpGeometryCopy(kind, offset, LpCopyValidationStatus.Valid, geometry, null);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or OverflowException)
        {
            return new LpGeometryCopy(
                kind,
                offset,
                LpCopyValidationStatus.Invalid,
                null,
                Diagnostic(source, offset, "geometry", "ImageFormats.Lp.InvalidGeometry", exception));
        }
    }

    private static LpGeometryCopy InvalidGeometry(
        IReadableBlockDevice source,
        LpMetadataCopyKind kind,
        long offset) =>
        new(
            kind,
            offset,
            LpCopyValidationStatus.Invalid,
            null,
            Diagnostic(source, offset, "geometry", "ImageFormats.Lp.InvalidGeometry"));

    private static void ValidateGeometryLimits(
        IReadableBlockDevice source,
        LpGeometry geometry,
        ImageReadLimits limits)
    {
        if (geometry.MetadataMaxSize == 0 || geometry.MetadataMaxSize % LpFormat.SectorSize != 0 ||
            geometry.MetadataSlotCount == 0 || geometry.MetadataSlotCount > limits.MaxLpMetadataSlots ||
            geometry.LogicalBlockSize < LpFormat.SectorSize ||
            geometry.LogicalBlockSize % LpFormat.SectorSize != 0)
        {
            throw Failure(
                source,
                ImageFormatErrorCode.CorruptMetadata,
                Resources.Errors_Lp_GeometrySizesInvalid,
                LpFormat.ReservedBytes,
                "geometry",
                "ImageFormats.Lp.InvalidGeometry");
        }

        if (geometry.MetadataMaxSize > limits.MaxLpMetadataBytes)
        {
            throw Failure(
                source,
                ImageFormatErrorCode.ResourceLimitExceeded,
                Resources.Errors_Lp_MetadataMaxSizeLimit,
                LpFormat.ReservedBytes + 40,
                "geometry",
                "ImageFormats.Lp.MetadataLimit");
        }
    }

    private static LpMetadataCopySummary ValidateMetadataCopy(
        IReadableBlockDevice source,
        LpGeometry geometry,
        int slot,
        LpMetadataCopyKind kind,
        ImageReadLimits limits,
        Span<byte> checksumBuffer)
    {
        long offset = GetMetadataOffset(geometry, slot, kind);
        try
        {
            LpMetadataHeader header = ReadAndValidateHeader(source, geometry, offset, slot, kind, limits);
            ValidateTablesChecksum(source, offset + header.HeaderSize, header.TablesSize, header, checksumBuffer);
            return new LpMetadataCopySummary(
                slot,
                kind,
                offset,
                LpCopyValidationStatus.Valid,
                header,
                null);
        }
        catch (ImageFormatException exception)
        {
            return new LpMetadataCopySummary(
                slot,
                kind,
                offset,
                LpCopyValidationStatus.Invalid,
                null,
                exception.Diagnostic);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or OverflowException)
        {
            return new LpMetadataCopySummary(
                slot,
                kind,
                offset,
                LpCopyValidationStatus.Invalid,
                null,
                Diagnostic(source, offset, "metadata", "ImageFormats.Lp.InvalidCopy", exception));
        }
    }

    private static LpMetadataHeader ReadAndValidateHeader(
        IReadableBlockDevice source,
        LpGeometry geometry,
        long offset,
        int slot,
        LpMetadataCopyKind kind,
        ImageReadLimits limits)
    {
        Span<byte> bytes = stackalloc byte[LpFormat.HeaderV12Size];
        BlockDeviceIO.ReadExactlyAt(source, offset, bytes[..LpFormat.HeaderV10Size]);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);

        if (magic != LpFormat.HeaderMagic)
            throw CopyFailure(source, slot, kind, offset, Resources.Errors_Lp_HeaderMagicInvalid, "ImageFormats.Lp.InvalidHeader");
        if (major != LpFormat.SupportedMajorVersion || minor > LpFormat.MaximumMinorVersion)
            throw CopyFailure(
                source,
                slot,
                kind,
                offset + 4,
                Resources.FormatErrors_Lp_UnsupportedMetadataVersion(major, minor),
                "ImageFormats.Lp.UnsupportedVersion",
                ImageFormatErrorCode.UnsupportedFeature,
                featureId: ((ulong)major << 16) | minor);

        int expectedHeaderSize = minor < 2 ? LpFormat.HeaderV10Size : LpFormat.HeaderV12Size;
        if (headerSize != expectedHeaderSize)
            throw CopyFailure(source, slot, kind, offset + 8, Resources.Errors_Lp_HeaderSizeInvalid, "ImageFormats.Lp.InvalidHeader");
        if (expectedHeaderSize > LpFormat.HeaderV10Size)
        {
            BlockDeviceIO.ReadExactlyAt(
                source,
                offset + LpFormat.HeaderV10Size,
                bytes.Slice(LpFormat.HeaderV10Size, expectedHeaderSize - LpFormat.HeaderV10Size));
        }

        Span<byte> expectedChecksum = stackalloc byte[32];
        bytes.Slice(12, 32).CopyTo(expectedChecksum);
        bytes.Slice(12, 32).Clear();
        Span<byte> computedChecksum = stackalloc byte[32];
        SHA256.HashData(bytes[..expectedHeaderSize], computedChecksum);
        if (!CryptographicOperations.FixedTimeEquals(expectedChecksum, computedChecksum))
            throw CopyFailure(source, slot, kind, offset + 12, Resources.Errors_Lp_HeaderChecksumMismatch, "ImageFormats.Lp.HeaderChecksum", ImageFormatErrorCode.ChecksumMismatch);

        uint tablesSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..]);
        if ((ulong)headerSize + tablesSize > geometry.MetadataMaxSize)
            throw CopyFailure(source, slot, kind, offset + 44, Resources.Errors_Lp_TablesExceedMetadataMaxSize, "ImageFormats.Lp.InvalidTables");

        LpTableDescriptor partitions = ReadDescriptor(bytes, 80);
        LpTableDescriptor extents = ReadDescriptor(bytes, 92);
        LpTableDescriptor groups = ReadDescriptor(bytes, 104);
        LpTableDescriptor blockDevices = ReadDescriptor(bytes, 116);
        ValidateDescriptor(source, offset, tablesSize, partitions, LpFormat.PartitionEntrySize, "partitions", slot, kind);
        ValidateDescriptor(source, offset, tablesSize, extents, LpFormat.ExtentEntrySize, "extents", slot, kind);
        ValidateDescriptor(source, offset, tablesSize, groups, LpFormat.GroupEntrySize, "groups", slot, kind);
        ValidateDescriptor(source, offset, tablesSize, blockDevices, LpFormat.BlockDeviceEntrySize, "block_devices", slot, kind);
        ValidateNoOverlap(source, offset, slot, kind, partitions, extents, groups, blockDevices);

        long totalEntries = checked(
            (long)partitions.EntryCount + extents.EntryCount + groups.EntryCount + blockDevices.EntryCount);
        if (totalEntries > limits.MaxLpTableEntries)
            throw CopyFailure(source, slot, kind, offset, Resources.Errors_Lp_TableEntryLimitExceeded, "ImageFormats.Lp.TableLimit", ImageFormatErrorCode.ResourceLimitExceeded);

        uint flags = expectedHeaderSize == LpFormat.HeaderV12Size
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[128..])
            : 0;
        return new LpMetadataHeader(
            major,
            minor,
            headerSize,
            tablesSize,
            partitions,
            extents,
            groups,
            blockDevices,
            flags);
    }

    private static void ValidateTablesChecksum(
        IReadableBlockDevice source,
        long tablesOffset,
        uint tablesSize,
        LpMetadataHeader header,
        Span<byte> buffer)
    {
        Span<byte> headerPrefix = stackalloc byte[80];
        BlockDeviceIO.ReadExactlyAt(source, tablesOffset - header.HeaderSize, headerPrefix);
        Span<byte> expected = stackalloc byte[32];
        headerPrefix.Slice(48, 32).CopyTo(expected);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long consumed = 0;
        while (consumed < tablesSize)
        {
            int length = (int)Math.Min(buffer.Length, tablesSize - consumed);
            Span<byte> part = buffer[..length];
            BlockDeviceIO.ReadExactlyAt(source, tablesOffset + consumed, part);
            hash.AppendData(part);
            consumed += length;
        }

        Span<byte> computed = stackalloc byte[32];
        hash.GetHashAndReset(computed);
        if (!CryptographicOperations.FixedTimeEquals(expected, computed))
        {
            throw Failure(
                source,
                ImageFormatErrorCode.ChecksumMismatch,
                Resources.Errors_Lp_TablesChecksumMismatch,
                tablesOffset,
                "tables",
                "ImageFormats.Lp.TablesChecksum");
        }
    }

    private static LpTableDescriptor ReadDescriptor(ReadOnlySpan<byte> header, int offset) =>
        new(
            BinaryPrimitives.ReadUInt32LittleEndian(header[offset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[(offset + 4)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[(offset + 8)..]));

    private static void ValidateDescriptor(
        IReadableBlockDevice source,
        long metadataOffset,
        uint tablesSize,
        LpTableDescriptor descriptor,
        int expectedEntrySize,
        string table,
        int slot,
        LpMetadataCopyKind kind)
    {
        if (descriptor.EntrySize != expectedEntrySize)
            throw CopyFailure(source, slot, kind, metadataOffset, Resources.FormatErrors_Lp_TableEntrySizeInvalid(table), "ImageFormats.Lp.InvalidTableDescriptor");
        ulong byteCount = (ulong)descriptor.EntryCount * descriptor.EntrySize;
        if (descriptor.Offset > tablesSize || byteCount > tablesSize - descriptor.Offset)
            throw CopyFailure(source, slot, kind, metadataOffset, Resources.FormatErrors_Lp_TableOutOfRange(table), "ImageFormats.Lp.InvalidTableDescriptor");
    }

    private static void ValidateNoOverlap(
        IReadableBlockDevice source,
        long metadataOffset,
        int slot,
        LpMetadataCopyKind kind,
        params LpTableDescriptor[] descriptors)
    {
        for (int left = 0; left < descriptors.Length; left++)
        {
            ulong leftSize = (ulong)descriptors[left].EntryCount * descriptors[left].EntrySize;
            if (leftSize == 0) continue;
            ulong leftEnd = descriptors[left].Offset + leftSize;
            for (int right = left + 1; right < descriptors.Length; right++)
            {
                ulong rightSize = (ulong)descriptors[right].EntryCount * descriptors[right].EntrySize;
                if (rightSize == 0) continue;
                ulong rightEnd = descriptors[right].Offset + rightSize;
                if (descriptors[left].Offset < rightEnd && descriptors[right].Offset < leftEnd)
                    throw CopyFailure(source, slot, kind, metadataOffset, Resources.Errors_Lp_TablesOverlap, "ImageFormats.Lp.OverlappingTables");
            }
        }
    }

    internal static long GetMetadataOffset(
        LpGeometry geometry,
        int slot,
        LpMetadataCopyKind kind)
    {
        long baseOffset = LpFormat.ReservedBytes + 2L * LpFormat.GeometryBlockSize;
        long copyOffset = kind == LpMetadataCopyKind.Primary
            ? 0
            : checked((long)geometry.MetadataMaxSize * geometry.MetadataSlotCount);
        return checked(baseOffset + copyOffset + (long)geometry.MetadataMaxSize * slot);
    }

    internal static ImageFormatException Failure(
        IReadableBlockDevice source,
        ImageFormatErrorCode code,
        string message,
        long offset,
        string structure,
        string resourceKey,
        ImageFormatDiagnosticReason? reason = null,
        string? objectId = null,
        ulong? featureId = null,
        Exception? innerException = null) =>
        new(
            code,
            message,
            Diagnostic(source, offset, structure, resourceKey, innerException, reason, objectId, featureId),
            innerException);

    private static ImageFormatException CopyFailure(
        IReadableBlockDevice source,
        int slot,
        LpMetadataCopyKind kind,
        long offset,
        string message,
        string resourceKey,
        ImageFormatErrorCode code = ImageFormatErrorCode.CorruptMetadata,
        ulong? featureId = null) =>
        Failure(
            source,
            code,
            message,
            offset,
            "metadata",
            resourceKey,
            objectId: $"slot:{slot}/{kind}",
            featureId: featureId);

    private static ImageFormatDiagnostic Diagnostic(
        IReadableBlockDevice source,
        long offset,
        string structure,
        string resourceKey,
        Exception? exception = null,
        ImageFormatDiagnosticReason? reason = null,
        string? objectId = null,
        ulong? featureId = null) =>
        new(
            "android-lp",
            structure,
            blockDeviceId: source.Id,
            deviceRelativeOffset: offset,
            objectId: objectId,
            featureId: featureId,
            reason: reason,
            resourceKey: resourceKey);
}
