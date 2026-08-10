
using System.Collections.ObjectModel;
using GeekFlashCore.Gpt.Internals;
using GeekFlashCore.Shared.Utilities;

namespace GeekFlashCore.Gpt;

internal sealed class GuidPartitionTable : IGpt
{
    
    private readonly GptLayout _layout;
    private readonly GptHeaderLayout _activeLayout;
    private readonly List<GptEntry> _entries;
    private readonly ReadOnlyCollection<GptEntry> _entriesView;
    private readonly bool _includeUnallocatedRegions;
    private IReadOnlyList<GptUnallocatedRegion> _unallocatedRegions;
    private IReadOnlyList<GptEntryOverlap> _overlaps;
    private byte[]? _entryStorage;
    private byte[] _headerTemplate;
    private GptHeader _header;

    public GuidPartitionTable(
        GptLayout layout,
        GptHeaderLayout activeLayout,
        GptHeader header,
        List<GptEntry> entries,
        byte[]? entryStorage,
        bool includeUnallocatedRegions)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(entries);
        _layout = layout;
        _activeLayout = activeLayout;
        _header = header;
        _entries = entries;
        _entriesView = entries.AsReadOnly();
        _entryStorage = entryStorage;
        _headerTemplate = activeLayout.HeaderBytes.ToArray();
        _includeUnallocatedRegions = includeUnallocatedRegions;
        _unallocatedRegions = CalculateUnallocatedRegionsIfRequested();
        _overlaps = CalculateOverlaps(_entries);
        CrcStatus = ValidateCrc();
    }

    public GptImageType ImageType => _layout.ImageType;
    public GptContainerType ContainerType => _layout.ContainerType;
    public int SectorSize => _layout.SectorSize;
    public ulong TotalDiskSectors
    {
        get
        {
            ulong finalHeaderLba = Math.Max(_header.CurrentLba, _header.AlternateLba);
            return finalHeaderLba > 1 ? checked(finalHeaderLba + 1) : 0;
        }
    }

    public int AvailableEntryCount => _activeLayout.AvailableEntryCount;
    public GptHeader Header => _header;
    public IReadOnlyList<GptEntry> Entries => _entriesView;
    public IReadOnlyList<GptUnallocatedRegion> UnallocatedRegions => _unallocatedRegions;
    public IReadOnlyList<GptEntryOverlap> Overlaps => _overlaps;
    public GptCrcStatus CrcStatus { get; private set; }
    public GptRedundancyStatus SourceRedundancyStatus =>
        _layout.RedundancyStatus with { ActiveCopy = _activeLayout.Copy };
    public GptRedundancyStatus RedundancyStatus => SourceRedundancyStatus;

    public GptCrcStatus ValidateCrc()
    {
        bool headerValid = ComputeHeaderCrc(_header, _headerTemplate) ==
                           _header.HeaderCrc32;
        bool? entriesValid = _entryStorage is null
            ? null
            : GptFormatValidator.IsEntryArrayCrcValid(_entryStorage, _header);
        return new GptCrcStatus(headerValid, entriesValid);
    }

    public void RepairCrc()
    {
        if (_entryStorage is not null)
            _header = _header with
            {
                PartitionEntryArrayCrc32 = Crc32Helper.Compute(
                    _entryStorage.AsSpan(
                        0,
                        GptFormatValidator.GetEntryArrayLength(_header)))
            };
        _header = _header with { HeaderCrc32 = 0 };
        _header = _header with
        {
            HeaderCrc32 = ComputeHeaderCrc(_header, _headerTemplate)
        };
        GptCodec.WriteHeader(_headerTemplate, _header);
        CrcStatus = ValidateCrc();
    }

    public void InsertPartition(int position, GptEntry partition)
    {
        EnsureEntriesLoaded();
        if ((uint)position > (uint)_entries.Count)
            throw new ArgumentOutOfRangeException(nameof(position));
        byte[] block = CreateEntryBlock(partition, preserveSlot: null);
        int slotIndex = FindFirstFreeSlot();
        WriteEntryBlock(slotIndex, block);
        _entries.Insert(position, CreateEntry(partition, slotIndex));
        CompleteMutation();
    }

    public void CreatePartition(GptEntry partition)
    {
        EnsureEntriesLoaded();
        byte[] block = CreateEntryBlock(partition, preserveSlot: null);
        int slotIndex = FindFirstFreeSlot();
        int position = _entries.FindIndex(entry => entry.FirstLba > partition.FirstLba);
        if (position < 0) position = _entries.Count;
        WriteEntryBlock(slotIndex, block);
        _entries.Insert(position, CreateEntry(partition, slotIndex));
        CompleteMutation();
    }

    public void UpdatePartition(int number, GptEntry partition)
    {
        EnsureEntriesLoaded();
        int position = FindPositionByNumber(number);
        GptEntry existing = _entries[position];
        byte[] block = CreateEntryBlock(partition, existing.SlotIndex);
        WriteEntryBlock(existing.SlotIndex, block);
        _entries[position] = CreateEntry(partition, existing.SlotIndex) with
        {
            Number = existing.Number
        };
        CompleteMutation();
    }

    public void DeletePartition(int number)
    {
        EnsureEntriesLoaded();
        int position = FindPositionByNumber(number);
        ClearEntryBlock(_entries[position].SlotIndex);
        _entries.RemoveAt(position);
        CompleteMutation();
    }

    public void MovePartition(int number, int newPosition)
    {
        EnsureEntriesLoaded();
        if ((uint)newPosition >= (uint)_entries.Count)
            throw new ArgumentOutOfRangeException(nameof(newPosition));
        int oldPosition = FindPositionByNumber(number);
        var items = _entries.Select(entry => new EntryWithBytes(
            entry,
            GetEntryBlock(entry.SlotIndex))).ToList();
        EntryWithBytes moved = items[oldPosition];
        items.RemoveAt(oldPosition);
        items.Insert(newPosition, moved);

        foreach (GptEntry entry in _entries)
            ClearEntryBlock(entry.SlotIndex);
        _entries.Clear();
        for (int index = 0; index < items.Count; index++)
        {
            EntryWithBytes item = items[index];
            WriteEntryBlock(index, item.Bytes);
            _entries.Add(item.Entry with { SlotIndex = index });
        }
        CompleteMutation();
    }

    public void MovePartitionToSlot(int number, int slotIndex)
    {
        EnsureEntriesLoaded();
        if ((uint)slotIndex >= (uint)AvailableEntryCount)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        int position = FindPositionByNumber(number);
        GptEntry entry = _entries[position];
        if (entry.SlotIndex == slotIndex) return;
        if (_entries.Any(item => item.SlotIndex == slotIndex))
            throw new InvalidOperationException(
                Strings.FormatEntrySlotOccupied(slotIndex));

        byte[] block = GetEntryBlock(entry.SlotIndex);
        ClearEntryBlock(entry.SlotIndex);
        WriteEntryBlock(slotIndex, block);
        _entries[position] = entry with { SlotIndex = slotIndex };
        CompleteMutation();
    }

    public void MovePartitionGeometry(int number, ulong firstLba)
    {
        EnsureEntriesLoaded();
        GptEntry entry = _entries[FindPositionByNumber(number)];
        ulong lastLba = entry.LastLba < entry.FirstLba
            ? firstLba == 0
                ? throw new ArgumentOutOfRangeException(nameof(firstLba))
                : firstLba - 1
            : checked(firstLba + entry.SectorCount - 1);
        UpdatePartition(number, entry with
        {
            FirstLba = firstLba,
            LastLba = lastLba
        });
    }

    public void ShiftPartition(int number, long sectorOffset)
    {
        EnsureEntriesLoaded();
        GptEntry entry = _entries[FindPositionByNumber(number)];
        ulong firstLba = AddSigned(entry.FirstLba, sectorOffset, nameof(sectorOffset));
        MovePartitionGeometry(number, firstLba);
    }

    public void SetPartitionEntryCount(int entryCount)
    {
        EnsureEntriesLoaded();
        if (entryCount < 1 || entryCount > AvailableEntryCount)
            throw new ArgumentOutOfRangeException(nameof(entryCount));
        int requiredCount = _entries.Count == 0
            ? 0
            : checked(_entries.Max(static entry => entry.SlotIndex) + 1);
        if (entryCount < requiredCount)
            throw new InvalidOperationException(Strings.EntryCountExcludesSlot);

        int oldCount = checked((int)_header.PartitionEntryCount);
        if (entryCount > oldCount)
        {
            for (int slot = oldCount; slot < entryCount; slot++)
                ClearEntryBlock(slot);
        }
        _header = _header with { PartitionEntryCount = checked((uint)entryCount) };
        RefreshDerivedState();
    }
    public byte[] ExportPartition(int number)
    {
        EnsureEntriesLoaded();
        GptEntry entry = _entries[FindPositionByNumber(number)];
        return GetEntryBlock(entry.SlotIndex);
    }

    public void Patch(ulong? lastUsableLba = null)
    {
        EnsureEntriesLoaded();
        lastUsableLba = NormalizeFullDiskPatchTarget(lastUsableLba);
        ApplyPatch(
            ref _header,
            _entries,
            _entryStorage!,
            lastUsableLba,
            _activeLayout.Copy,
            GetPatchEntryArraySectorCount(ContainerType == GptContainerType.FullDisk),
            preservePartitionEntryLba: ContainerType == GptContainerType.FullDisk);
        RefreshDerivedState();
    }

    public void Unpatch(ulong? lastUsableLba = null)
    {
        EnsureEntriesLoaded();
        if (ContainerType != GptContainerType.Compact ||
            ImageType is not (GptImageType.Main or GptImageType.Backup))
            throw new InvalidOperationException(Strings.UnpatchRequiresSingleCompactCopy);
        ApplyUnpatch(ref _header, _entries, _entryStorage!, lastUsableLba, ImageType);
        RefreshDerivedState();
    }

    public void WriteTo(Stream destination, GptExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException(
                Strings.OutputStreamMustBeWritableAndSeekable,
                nameof(destination));

        EnsureEntriesLoaded();
        options ??= new GptExportOptions();
        GptImageType outputType = ResolveOutputType(options);
        bool preserveFullDisk = ShouldPreserveFullDisk(options);
        GptWriteSnapshot snapshot = CreateWriteSnapshot(
            options,
            outputType,
            preserveFullDisk);
        if (preserveFullDisk)
        {
            GptWriter.WritePreservedFullDisk(destination, snapshot);
            return;
        }

        byte[] output = GC.AllocateUninitializedArray<byte>(
            GptWriter.GetOutputLength(snapshot, outputType, preserveFullDisk: false));
        GptWriter.Write(output, snapshot, outputType, preserveFullDisk: false);
        destination.Seek(0, SeekOrigin.Begin);
        destination.Write(output);
        destination.SetLength(output.Length);
    }

    public byte[] ToArray(GptExportOptions? options = null)
    {
        EnsureEntriesLoaded();
        options ??= new GptExportOptions();
        GptImageType outputType = ResolveOutputType(options);
        bool preserveFullDisk = ShouldPreserveFullDisk(options);
        GptWriteSnapshot snapshot = CreateWriteSnapshot(
            options,
            outputType,
            preserveFullDisk);
        byte[] output = GC.AllocateUninitializedArray<byte>(
            GptWriter.GetOutputLength(snapshot, outputType, preserveFullDisk));
        GptWriter.Write(output, snapshot, outputType, preserveFullDisk);
        return output;
    }

    public void SaveFile(string path, GptExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException(Strings.OutputPathHasNoParent);
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                WriteTo(stream, options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private GptWriteSnapshot CreateWriteSnapshot(
        GptExportOptions options,
        GptImageType outputType,
        bool preserveFullDisk)
    {
        var entries = _entries.ToList();
        byte[] entryStorage = _entryStorage!.ToArray();
        GptHeader header = _header;
        switch (options.PatchMode)
        {
            case GptPatchMode.None:
                break;
            case GptPatchMode.Patch:
                ulong? patchTarget = preserveFullDisk
                    ? NormalizeFullDiskPatchTarget(options.LastUsableLba)
                    : options.LastUsableLba;
                ApplyPatch(
                    ref header,
                    entries,
                    entryStorage,
                    patchTarget,
                    preserveFullDisk
                        ? _activeLayout.Copy
                        : outputType == GptImageType.Backup
                            ? GptHeaderCopy.Backup
                            : GptHeaderCopy.Primary,
                    GetPatchEntryArraySectorCount(preserveFullDisk),
                    preservePartitionEntryLba: preserveFullDisk);
                break;
            case GptPatchMode.Unpatch:
                if (preserveFullDisk ||
                    outputType is GptImageType.Both or GptImageType.SgdiskBackup)
                    throw new InvalidOperationException(
                        Strings.UnpatchRequiresSingleCompactCopy);
                ApplyUnpatch(ref header, entries, entryStorage, options.LastUsableLba, outputType);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        return new GptWriteSnapshot(
            _layout,
            AvailableEntryCount,
            header,
            _headerTemplate.ToArray(),
            entryStorage);
    }

    private GptImageType ResolveOutputType(GptExportOptions options)
    {
        GptImageType imageType = options.ImageType ?? ImageType;
        if (imageType == GptImageType.Unknown)
            throw new NotSupportedException(
                Strings.FormatImageTypeCannotBeExported(imageType));
        bool isUnpatchedBackup = imageType == GptImageType.Backup &&
                                 ImageType == GptImageType.Backup &&
                                 _header.CurrentLba == 0 &&
                                 _header.AlternateLba == 1 &&
                                 _header.PartitionEntryLba == 0;
        if (imageType is GptImageType.Backup or GptImageType.Both &&
            ResolveBackupLba(_header) <= 1 &&
            options.PatchMode != GptPatchMode.Patch &&
            !isUnpatchedBackup)
            throw new InvalidOperationException(Strings.BackupExportRequiresGeometry);
        return imageType;
    }

    private bool ShouldPreserveFullDisk(GptExportOptions options) =>
        ContainerType == GptContainerType.FullDisk &&
        options.ImageType is null &&
        options.PreserveFullDiskImage;

    private GptEntry CreateEntry(GptEntry partition, int slotIndex) =>
        new(
            0,
            slotIndex,
            partition.TypeId,
            partition.Id,
            partition.FirstLba,
            partition.LastLba,
            partition.Attributes,
            partition.Name);

    private byte[] CreateEntryBlock(GptEntry partition, int? preserveSlot)
    {
        ValidatePartition(partition, preserveSlot);
        byte[] block = preserveSlot is int slot
            ? GetEntryBlock(slot)
            : new byte[checked((int)_header.PartitionEntrySize)];
        GptCodec.WriteEntry(block, partition);
        return block;
    }

    private void ValidatePartition(GptEntry partition, int? excludedSlotIndex)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (partition.Id == Guid.Empty)
            throw new ArgumentException(
                Strings.PartitionIdCannotBeEmpty,
                nameof(partition));
        if (_entries.Any(entry =>
                entry.SlotIndex != excludedSlotIndex &&
                entry.Id == partition.Id))
            throw new ArgumentException(
                Strings.FormatPartitionIdMustBeUnique(partition.Id),
                nameof(partition));
        bool sentinel = partition.FirstLba > 0 &&
                        partition.LastLba == partition.FirstLba - 1;
        if (partition.FirstLba > partition.LastLba && !sentinel)
            throw new ArgumentException(Strings.FirstLbaExceedsLastLba, nameof(partition));
        if (partition.FirstLba < _header.FirstUsableLba)
            throw new ArgumentOutOfRangeException(
                nameof(partition),
                Strings.PartitionOutsideUsableRange);
        bool hasUsableRange = _header.LastUsableLba >= _header.FirstUsableLba &&
                              _header.LastUsableLba != 0;
        if (hasUsableRange && !sentinel && partition.LastLba > _header.LastUsableLba)
            throw new ArgumentOutOfRangeException(
                nameof(partition),
                Strings.PartitionOutsideUsableRange);

        GptCodec.WriteEntry(
            new byte[checked((int)_header.PartitionEntrySize)],
            partition);
    }

    private int FindFirstFreeSlot()
    {
        if (_entries.Count >= AvailableEntryCount)
            throw new InvalidOperationException(Strings.NoPartitionEntrySlotsAvailable);
        Span<bool> used = AvailableEntryCount <= 256
            ? stackalloc bool[AvailableEntryCount]
            : new bool[AvailableEntryCount];
        foreach (GptEntry entry in _entries)
            used[entry.SlotIndex] = true;
        return used.IndexOf(false);
    }

    private int FindPositionByNumber(int number)
    {
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
        int position = _entries.FindIndex(entry => entry.Number == number);
        if (position < 0)
            throw new ArgumentException(
                Strings.FormatPartitionNumberNotFound(number),
                nameof(number));
        return position;
    }

    private void CompleteMutation()
    {
        for (int position = 0; position < _entries.Count; position++)
            _entries[position] = _entries[position] with { Number = position + 1 };

        int requiredEntryCount = _entries.Count == 0
            ? 0
            : checked(_entries.Max(static entry => entry.SlotIndex) + 1);
        if (requiredEntryCount > AvailableEntryCount)
            throw new InvalidOperationException(Strings.PartitionEntriesExceedCapacity);
        if ((uint)requiredEntryCount > _header.PartitionEntryCount)
        {
            var occupiedSlots = _entries
                .Select(static entry => entry.SlotIndex)
                .ToHashSet();
            for (int slot = checked((int)_header.PartitionEntryCount);
                 slot < requiredEntryCount;
                 slot++)
            {
                if (!occupiedSlots.Contains(slot)) ClearEntryBlock(slot);
            }
            _header = _header with { PartitionEntryCount = checked((uint)requiredEntryCount) };
        }
        RefreshDerivedState();
    }

    private void RefreshDerivedState()
    {
        _unallocatedRegions = CalculateUnallocatedRegionsIfRequested();
        _overlaps = CalculateOverlaps(_entries);
        CrcStatus = ValidateCrc();
    }

    private void EnsureEntriesLoaded()
    {
        if (_entryStorage is null)
            throw new InvalidOperationException(Strings.HeaderOnlyDocumentCannotBeEdited);
    }

    private byte[] GetEntryBlock(int slotIndex)
    {
        int entrySize = checked((int)_header.PartitionEntrySize);
        return _entryStorage!.AsSpan(checked(slotIndex * entrySize), entrySize).ToArray();
    }

    private void WriteEntryBlock(int slotIndex, ReadOnlySpan<byte> block)
    {
        int entrySize = checked((int)_header.PartitionEntrySize);
        if (block.Length != entrySize)
            throw new ArgumentException(Strings.EntryBlockUnexpectedLength, nameof(block));
        block.CopyTo(_entryStorage!.AsSpan(checked(slotIndex * entrySize), entrySize));
    }

    private void ClearEntryBlock(int slotIndex)
    {
        int entrySize = checked((int)_header.PartitionEntrySize);
        _entryStorage!.AsSpan(checked(slotIndex * entrySize), entrySize).Clear();
    }

    private IReadOnlyList<GptUnallocatedRegion> CalculateUnallocatedRegionsIfRequested() =>
        _includeUnallocatedRegions
            ? CalculateUnallocatedRegions(
                _entries,
                _header.FirstUsableLba,
                _header.LastUsableLba)
            : Array.Empty<GptUnallocatedRegion>();

    private static uint ComputeHeaderCrc(
        GptHeader header,
        ReadOnlySpan<byte> template)
    {
        int headerSize = checked((int)header.HeaderSize);
        if (template.Length < headerSize)
            throw new InvalidDataException(Strings.PreservedHeaderTooShort);
        byte[] buffer = template[..headerSize].ToArray();
        GptCodec.WriteHeader(buffer, header with { HeaderCrc32 = 0 });
        return Crc32Helper.Compute(buffer);
    }

    private void ApplyPatch(
        ref GptHeader header,
        List<GptEntry> entries,
        byte[] entryStorage,
        ulong? lastUsableLba,
        GptHeaderCopy headerCopy,
        ulong entryArraySectors,
        bool preservePartitionEntryLba)
    {
        int position = FindSpatialLastPosition(entries);
        GptEntry last = entries[position];
        ulong target = lastUsableLba ?? ResolvePatchTarget(header, last, entryArraySectors);
        if (target < last.FirstLba)
            throw new ArgumentOutOfRangeException(nameof(lastUsableLba));
        GptEntry updatedEntry = last with { LastLba = target };
        ulong backupHeaderLba = checked(target + entryArraySectors + 1);
        ulong partitionEntryLba = preservePartitionEntryLba
            ? header.PartitionEntryLba
            : headerCopy == GptHeaderCopy.Backup
                ? checked(target + 1)
                : 2;
        GptHeader updatedHeader = headerCopy == GptHeaderCopy.Backup
            ? header with
            {
                CurrentLba = backupHeaderLba,
                AlternateLba = 1,
                LastUsableLba = target,
                PartitionEntryLba = partitionEntryLba
            }
            : header with
            {
                CurrentLba = 1,
                AlternateLba = backupHeaderLba,
                LastUsableLba = target,
                PartitionEntryLba = partitionEntryLba
            };
        byte[] updatedBlock = CreateUpdatedEntryBlock(
            entryStorage,
            header.PartitionEntrySize,
            updatedEntry);

        entries[position] = updatedEntry;
        WriteEntryBlock(entryStorage, header.PartitionEntrySize, updatedEntry.SlotIndex, updatedBlock);
        header = updatedHeader;
    }

    private void ApplyUnpatch(
        ref GptHeader header,
        List<GptEntry> entries,
        byte[] entryStorage,
        ulong? lastUsableLba,
        GptImageType imageType)
    {
        int position = FindSpatialLastPosition(entries);
        GptEntry last = entries[position];
        if (last.FirstLba == 0)
            throw new InvalidOperationException(Strings.UnpatchWouldUnderflow);
        GptEntry updatedEntry = last with { LastLba = last.FirstLba - 1 };
        GptHeader updatedHeader = imageType == GptImageType.Backup
            ? header with
            {
                CurrentLba = 0,
                AlternateLba = 1,
                LastUsableLba = lastUsableLba ?? 0,
                PartitionEntryLba = 0
            }
            : header with
            {
                CurrentLba = 1,
                AlternateLba = 0,
                LastUsableLba = lastUsableLba ?? 0,
                PartitionEntryLba = 2
            };
        byte[] updatedBlock = CreateUpdatedEntryBlock(
            entryStorage,
            header.PartitionEntrySize,
            updatedEntry);

        entries[position] = updatedEntry;
        WriteEntryBlock(entryStorage, header.PartitionEntrySize, updatedEntry.SlotIndex, updatedBlock);
        header = updatedHeader;
    }

    private static int FindSpatialLastPosition(IReadOnlyList<GptEntry> entries)
    {
        if (entries.Count == 0)
            throw new InvalidOperationException(Strings.PatchRequiresPartition);
        int result = 0;
        for (int position = 1; position < entries.Count; position++)
        {
            if (entries[position].FirstLba > entries[result].FirstLba ||
                entries[position].FirstLba == entries[result].FirstLba &&
                entries[position].LastLba > entries[result].LastLba)
                result = position;
        }
        return result;
    }

    private ulong ResolvePatchTarget(
        GptHeader header,
        GptEntry last,
        ulong entryArraySectors)
    {
        if (header.LastUsableLba >= last.FirstLba)
            return header.LastUsableLba;
        ulong backupLba = ResolveBackupLba(header);
        if (backupLba > entryArraySectors + 1)
            return checked(backupLba - entryArraySectors - 1);
        throw new InvalidOperationException(Strings.PatchRequiresLastUsableLba);
    }

    private ulong GetPatchEntryArraySectorCount(bool preserveFullDisk)
    {
        if (preserveFullDisk)
        {
            int capacityBytes = Math.Max(
                _layout.MainHeader?.CapacityBytes ?? 0,
                _layout.BackupHeader?.CapacityBytes ?? 0);
            if (capacityBytes > 0)
                return GptFormatValidator.GetSectorCountForBytes(
                    checked((ulong)capacityBytes),
                    SectorSize);
        }
        return GptFormatValidator.GetPhysicalEntryArraySectorCount(
            AvailableEntryCount,
            _header.PartitionEntrySize,
            SectorSize);
    }

    private ulong? NormalizeFullDiskPatchTarget(ulong? requested)
    {
        if (ContainerType != GptContainerType.FullDisk) return requested;
        ulong entryArraySectors = GetPatchEntryArraySectorCount(preserveFullDisk: true);
        ulong backupHeaderLba;
        if (_layout.BackupHeader is { } backup)
        {
            backupHeaderLba = checked((ulong)(backup.HeaderOffset / SectorSize));
            if (backupHeaderLba != backup.Header.CurrentLba)
                throw new InvalidOperationException(Strings.GeometryContainerMismatch);
        }
        else
        {
            backupHeaderLba = checked((ulong)(_layout.SourceLength / SectorSize) - 1);
            ulong declaredBackupLba = ResolveBackupLba(_header);
            if (declaredBackupLba > 1 && declaredBackupLba != backupHeaderLba)
                throw new InvalidOperationException(Strings.GeometryContainerMismatch);
        }
        if (backupHeaderLba <= entryArraySectors)
            throw new InvalidOperationException(Strings.BackupExportRequiresGeometry);
        ulong expected = checked(backupHeaderLba - entryArraySectors - 1);
        if (requested is not null && requested.Value != expected)
            throw new ArgumentOutOfRangeException(nameof(requested));
        return expected;
    }

    private static byte[] CreateUpdatedEntryBlock(
        byte[] storage,
        uint entrySizeValue,
        GptEntry entry)
    {
        int entrySize = checked((int)entrySizeValue);
        byte[] block = storage.AsSpan(
            checked(entry.SlotIndex * entrySize),
            entrySize).ToArray();
        GptCodec.WriteEntry(block, entry);
        return block;
    }

    private static void WriteEntryBlock(
        byte[] storage,
        uint entrySizeValue,
        int slotIndex,
        ReadOnlySpan<byte> block)
    {
        int entrySize = checked((int)entrySizeValue);
        block.CopyTo(storage.AsSpan(checked(slotIndex * entrySize), entrySize));
    }

    private static ulong ResolveBackupLba(GptHeader header) =>
        Math.Max(header.CurrentLba, header.AlternateLba);

    private static ulong AddSigned(ulong value, long delta, string parameterName)
    {
        if (delta >= 0)
        {
            try
            {
                return checked(value + (ulong)delta);
            }
            catch (OverflowException exception)
            {
                throw new ArgumentOutOfRangeException(parameterName, exception.Message);
            }
        }

        ulong magnitude = checked((ulong)(-(delta + 1)) + 1);
        if (magnitude > value)
            throw new ArgumentOutOfRangeException(parameterName);
        return value - magnitude;
    }

    private static IReadOnlyList<GptUnallocatedRegion> CalculateUnallocatedRegions(
        IReadOnlyList<GptEntry> entries,
        ulong firstUsableLba,
        ulong lastUsableLba)
    {
        if (lastUsableLba < firstUsableLba || lastUsableLba == 0)
            return Array.Empty<GptUnallocatedRegion>();
        GptEntry[] sorted = entries
            .Where(entry => entry.LastLba >= entry.FirstLba)
            .OrderBy(static entry => entry.FirstLba)
            .ThenBy(static entry => entry.LastLba)
            .ToArray();
        var regions = new List<GptUnallocatedRegion>();
        ulong current = firstUsableLba;
        foreach (GptEntry entry in sorted)
        {
            if (entry.LastLba < firstUsableLba || entry.FirstLba > lastUsableLba)
                continue;
            ulong start = Math.Max(entry.FirstLba, firstUsableLba);
            ulong end = Math.Min(entry.LastLba, lastUsableLba);
            if (current < start)
                regions.Add(new GptUnallocatedRegion(current, start - 1));
            if (end == ulong.MaxValue)
                return regions.AsReadOnly();
            current = Math.Max(current, end + 1);
        }
        if (current <= lastUsableLba)
            regions.Add(new GptUnallocatedRegion(current, lastUsableLba));
        return regions.AsReadOnly();
    }

    private static IReadOnlyList<GptEntryOverlap> CalculateOverlaps(
        IReadOnlyList<GptEntry> entries)
    {
        GptEntry[] sorted = entries
            .Where(entry => entry.LastLba >= entry.FirstLba)
            .OrderBy(static entry => entry.FirstLba)
            .ThenBy(static entry => entry.LastLba)
            .ToArray();
        var overlaps = new List<GptEntryOverlap>();
        for (int left = 0; left < sorted.Length; left++)
        {
            for (int right = left + 1; right < sorted.Length; right++)
            {
                if (sorted[right].FirstLba > sorted[left].LastLba) break;
                overlaps.Add(new GptEntryOverlap(
                    sorted[left].Number,
                    sorted[right].Number,
                    sorted[right].FirstLba,
                    Math.Min(sorted[left].LastLba, sorted[right].LastLba)));
            }
        }
        return overlaps.AsReadOnly();
    }
    private static Dictionary<string, (int? A, int? B)> BuildAbGroups(
        IReadOnlyList<GptEntry> entries)
    {
        var groups = new Dictionary<string, (int? A, int? B)>(
            StringComparer.OrdinalIgnoreCase);
        for (int position = 0; position < entries.Count; position++)
        {
            string name = entries[position].Name;
            if (name.EndsWith("_a", StringComparison.OrdinalIgnoreCase))
            {
                string stem = name[..^2];
                groups.TryGetValue(stem, out (int? A, int? B) pair);
                groups[stem] = (position, pair.B);
            }
            else if (name.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
            {
                string stem = name[..^2];
                groups.TryGetValue(stem, out (int? A, int? B) pair);
                groups[stem] = (pair.A, position);
            }
        }
        return groups;
    }

    private sealed record EntryWithBytes(GptEntry Entry, byte[] Bytes);
}
