using GeekFlashCore.Android.Lp.Abstractions;

namespace GeekFlashCore.Android.Lp;

public sealed record LpDraftPartition(
    LpPartitionHandle Handle,
    string RawName,
    string Name,
    LpPartitionAttributes Attributes,
    string GroupName,
    long LogicalSize,
    bool HasReplacementImage,
    bool IsNew);

public sealed record LpDraftGroup(
    LpGroupHandle Handle,
    string RawName,
    string Name,
    LpGroupFlags Flags,
    ulong MaximumSize,
    bool IsDefault,
    bool IsNew);

public sealed class LpDraft
{
    private readonly LpEditSession _session;
    private readonly object _sync = new();
    private readonly List<LpPartitionState> _partitions;
    private readonly LpPartitionState[] _originalPartitions;
    private readonly List<LpGroupState> _groups;
    private int _nextPartitionHandle;
    private int _nextGroupHandle;

    internal LpDraft(LpEditSession session, LpTableModel baseline)
    {
        _session = session;
        _groups = new List<LpGroupState>(baseline.Groups.Length);
        for (int index = 0; index < baseline.Groups.Length; index++)
        {
            LpPartitionGroup group = baseline.Groups[index];
            var handle = new LpGroupHandle(++_nextGroupHandle);
            _groups.Add(new LpGroupState(
                handle,
                group.RawName,
                group.Flags,
                group.MaximumSize,
                string.Equals(group.RawName, "default", StringComparison.Ordinal),
                IsNew: false));
        }

        _partitions = new List<LpPartitionState>(baseline.Partitions.Length);
        for (int index = 0; index < baseline.Partitions.Length; index++)
        {
            LpPartition partition = baseline.Partitions[index];
            var extents = new LpExtent[partition.ExtentCount];
            baseline.Extents.AsSpan(
                checked((int)partition.FirstExtentIndex),
                checked((int)partition.ExtentCount)).CopyTo(extents);
            var handle = new LpPartitionHandle(++_nextPartitionHandle);
            _partitions.Add(new LpPartitionState(
                handle,
                partition.RawName,
                partition.Attributes,
                baseline.Groups[checked((int)partition.GroupIndex)].RawName,
                partition.LogicalSize,
                extents,
                ReplacementImage: null,
                IsNew: false));
        }
        _originalPartitions = _partitions.ToArray();

        ValidateUniqueNames();
    }

    public IReadOnlyList<LpDraftPartition> Partitions
    {
        get
        {
            lock (_sync)
            {
                return _partitions.Select(ToView).ToArray();
            }
        }
    }

    public IReadOnlyList<LpDraftGroup> Groups
    {
        get
        {
            lock (_sync)
            {
                return _groups.Select(ToView).ToArray();
            }
        }
    }

    public LpPartitionHandle AddPartition(
        string rawName,
        string groupName,
        LpPartitionAttributes attributes,
        long initialSize = 0)
    {
        LpNameValidator.Validate(rawName, nameof(rawName));
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        LpNameValidator.ValidatePartitionAttributes(
            attributes,
            _session.MetadataMinorVersion,
            nameof(attributes));
        if (initialSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialSize),
                Resources.InvalidLogicalSize);
        }

        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            _ = GetGroupState(groupName);
            EnsurePartitionNameAvailable(rawName, attributes, except: null);
            var handle = new LpPartitionHandle(++_nextPartitionHandle);
            _partitions.Add(new LpPartitionState(
                handle,
                rawName,
                attributes,
                groupName,
                AlignSize(initialSize),
                [],
                ReplacementImage: null,
                IsNew: true));
            return handle;
        }
    }

    public void RemovePartition(LpPartitionHandle partition)
    {
        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            int index = GetPartitionIndex(partition);
            _partitions.RemoveAt(index);
        }
    }

    public void RenamePartition(LpPartitionHandle partition, string rawName)
    {
        LpNameValidator.Validate(rawName, nameof(rawName));
        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            int index = GetPartitionIndex(partition);
            LpPartitionState current = _partitions[index];
            EnsurePartitionNameAvailable(rawName, current.Attributes, current.Handle);
            _partitions[index] = current with { RawName = rawName };
        }
    }

    public void SetPartitionAttributes(
        LpPartitionHandle partition,
        LpPartitionAttributes attributes)
    {
        LpNameValidator.ValidatePartitionAttributes(
            attributes,
            _session.MetadataMinorVersion,
            nameof(attributes));
        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            int index = GetPartitionIndex(partition);
            LpPartitionState current = _partitions[index];
            EnsurePartitionNameAvailable(current.RawName, attributes, current.Handle);
            _partitions[index] = current with { Attributes = attributes };
        }
    }

    public void MovePartitionToGroup(LpPartitionHandle partition, string groupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            _ = GetGroupState(groupName);
            int index = GetPartitionIndex(partition);
            _partitions[index] = _partitions[index] with { GroupRawName = groupName };
        }
    }

    public void ResizePartition(LpPartitionHandle partition, long requestedBytes)
    {
        if (requestedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedBytes),
                Resources.InvalidLogicalSize);
        }

        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            int index = GetPartitionIndex(partition);
            LpPartitionState current = _partitions[index];
            long alignedSize = AlignSize(requestedBytes);
            if (current.ReplacementImage is not null &&
                alignedSize < current.ReplacementImage.GetAlignedLength(
                    _session.Geometry.LogicalBlockSize))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestedBytes),
                    Resources.ImageTooLargeForPartition);
            }

            _partitions[index] = current with { RequestedSize = alignedSize };
        }
    }

    public void ReplacePartitionImage(
        LpPartitionHandle partition,
        LpPartitionImageSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            int index = GetPartitionIndex(partition);
            if (!image.CanReplay && _partitions.Any(candidate =>
                candidate.Handle != partition &&
                ReferenceEquals(candidate.ReplacementImage, image)))
            {
                throw new InvalidOperationException(
                    Resources.NonReplayableImageAlreadyAssigned);
            }
            _session.RegisterImage(image);
            _partitions[index] = _partitions[index] with
            {
                ReplacementImage = image,
                RequestedSize = image.GetAlignedLength(_session.Geometry.LogicalBlockSize)
            };
        }
    }

    public void AddGroup(
        string name,
        ulong maximumSize,
        LpGroupFlags flags = LpGroupFlags.None)
    {
        LpNameValidator.Validate(name, nameof(name));
        LpNameValidator.ValidateGroupFlags(flags, nameof(flags));
        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            EnsureGroupNameAvailable(name, flags, except: null);
            _groups.Add(new LpGroupState(
                new LpGroupHandle(++_nextGroupHandle),
                name,
                flags,
                maximumSize,
                IsDefault: false,
                IsNew: true));
        }
    }

    public void RenameGroup(string name, string newName)
    {
        LpNameValidator.Validate(newName, nameof(newName));
        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            int index = GetGroupIndex(name);
            LpGroupState current = _groups[index];
            ThrowIfDefault(current);
            EnsureGroupNameAvailable(newName, current.Flags, current.Handle);
            _groups[index] = current with { RawName = newName };
            for (int partitionIndex = 0; partitionIndex < _partitions.Count; partitionIndex++)
            {
                if (string.Equals(
                    _partitions[partitionIndex].GroupRawName,
                    current.RawName,
                    StringComparison.Ordinal))
                {
                    _partitions[partitionIndex] = _partitions[partitionIndex] with
                    {
                        GroupRawName = newName
                    };
                }
            }
        }
    }

    public void SetGroupMaximumSize(string name, ulong maximumSize)
    {
        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            int index = GetGroupIndex(name);
            ThrowIfDefault(_groups[index]);
            _groups[index] = _groups[index] with { MaximumSize = maximumSize };
        }
    }

    public void SetGroupFlags(string name, LpGroupFlags flags)
    {
        LpNameValidator.ValidateGroupFlags(flags, nameof(flags));
        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            int index = GetGroupIndex(name);
            LpGroupState current = _groups[index];
            ThrowIfDefault(current);
            EnsureGroupNameAvailable(current.RawName, flags, current.Handle);
            _groups[index] = current with { Flags = flags };
        }
    }

    public void RemoveGroup(string name, LpGroupRemovalMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        using LpEditSession.MutationLease mutation = _session.EnterMutation();
        lock (_sync)
        {
            int index = GetGroupIndex(name);
            LpGroupState group = _groups[index];
            ThrowIfDefault(group);
            bool hasPartitions = _partitions.Any(partition =>
                string.Equals(partition.GroupRawName, group.RawName, StringComparison.Ordinal));
            if (hasPartitions && mode == LpGroupRemovalMode.FailIfNotEmpty)
            {
                throw new InvalidOperationException(
                    Resources.FormatGroupNotEmpty(group.RawName));
            }

            if (hasPartitions)
            {
                _partitions.RemoveAll(partition =>
                    string.Equals(partition.GroupRawName, group.RawName, StringComparison.Ordinal));
            }
            _groups.RemoveAt(index);
        }
    }

    public LpPartitionHandle FindPartition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            LpPartitionState? state = _partitions.FirstOrDefault(partition =>
                string.Equals(partition.RawName, name, StringComparison.Ordinal) ||
                string.Equals(GetEffectivePartitionName(partition), name, StringComparison.Ordinal));
            return state?.Handle ?? throw new KeyNotFoundException(
                Resources.PartitionNotFound);
        }
    }

    public LpDraftPartition GetPartition(LpPartitionHandle partition)
    {
        lock (_sync)
        {
            return ToView(_partitions[GetPartitionIndex(partition)]);
        }
    }

    internal LpDraftSnapshot CaptureSnapshot()
    {
        lock (_sync)
        {
            var partitions = new LpPartitionState[_partitions.Count];
            for (int index = 0; index < partitions.Length; index++)
            {
                LpPartitionState state = _partitions[index];
                partitions[index] = state with
                {
                    OriginalExtents = state.OriginalExtents.ToArray()
                };
            }
            var originals = new LpPartitionState[_originalPartitions.Length];
            for (int index = 0; index < originals.Length; index++)
            {
                originals[index] = _originalPartitions[index] with
                {
                    OriginalExtents = _originalPartitions[index].OriginalExtents.ToArray()
                };
            }
            return new LpDraftSnapshot(partitions, _groups.ToArray(), originals);
        }
    }

    private LpDraftPartition ToView(LpPartitionState state) => new(
        state.Handle,
        state.RawName,
        GetEffectivePartitionName(state),
        state.Attributes,
        state.GroupRawName,
        state.RequestedSize,
        state.ReplacementImage is not null,
        state.IsNew);

    private LpDraftGroup ToView(LpGroupState state) => new(
        state.Handle,
        state.RawName,
        LpNameValidator.GetEffectiveName(
            state.RawName,
            (state.Flags & LpGroupFlags.SlotSuffixed) != 0,
            _session.SlotNumber),
        state.Flags,
        state.MaximumSize,
        state.IsDefault,
        state.IsNew);

    private string GetEffectivePartitionName(LpPartitionState state) =>
        LpNameValidator.GetEffectiveName(
            state.RawName,
            (state.Attributes & LpPartitionAttributes.SlotSuffixed) != 0,
            _session.SlotNumber);

    private long AlignSize(long size)
    {
        long blockSize = _session.Geometry.LogicalBlockSize;
        if (size == 0)
        {
            return 0;
        }
        return checked(((size + blockSize - 1) / blockSize) * blockSize);
    }

    private int GetPartitionIndex(LpPartitionHandle handle)
    {
        if (handle.IsEmpty)
        {
            throw new ArgumentException(Resources.PartitionNotFound, nameof(handle));
        }

        int index = _partitions.FindIndex(candidate => candidate.Handle == handle);
        return index >= 0
            ? index
            : throw new KeyNotFoundException(Resources.PartitionNotFound);
    }

    private LpGroupState GetGroupState(string name) => _groups[GetGroupIndex(name)];

    private int GetGroupIndex(string name)
    {
        int index = _groups.FindIndex(candidate =>
            string.Equals(candidate.RawName, name, StringComparison.Ordinal));
        return index >= 0
            ? index
            : throw new KeyNotFoundException(Resources.GroupNotFound);
    }

    private void EnsurePartitionNameAvailable(
        string rawName,
        LpPartitionAttributes attributes,
        LpPartitionHandle? except)
    {
        string effectiveName = LpNameValidator.GetEffectiveName(
            rawName,
            (attributes & LpPartitionAttributes.SlotSuffixed) != 0,
            _session.SlotNumber);
        bool duplicate = _partitions.Any(candidate =>
            candidate.Handle != except &&
            (string.Equals(candidate.RawName, rawName, StringComparison.Ordinal) ||
             string.Equals(GetEffectivePartitionName(candidate), effectiveName, StringComparison.Ordinal)));
        if (duplicate)
        {
            throw new ArgumentException(
                Resources.FormatDuplicateName(rawName),
                nameof(rawName));
        }
    }

    private void EnsureGroupNameAvailable(
        string rawName,
        LpGroupFlags flags,
        LpGroupHandle? except)
    {
        string effectiveName = LpNameValidator.GetEffectiveName(
            rawName,
            (flags & LpGroupFlags.SlotSuffixed) != 0,
            _session.SlotNumber);
        bool duplicate = _groups.Any(candidate =>
            candidate.Handle != except &&
            (string.Equals(candidate.RawName, rawName, StringComparison.Ordinal) ||
             string.Equals(
                 LpNameValidator.GetEffectiveName(
                     candidate.RawName,
                     (candidate.Flags & LpGroupFlags.SlotSuffixed) != 0,
                     _session.SlotNumber),
                 effectiveName,
                 StringComparison.Ordinal)));
        if (duplicate)
        {
            throw new ArgumentException(
                Resources.FormatDuplicateName(rawName),
                nameof(rawName));
        }
    }

    private void ValidateUniqueNames()
    {
        var partitionRawNames = new HashSet<string>(StringComparer.Ordinal);
        var partitionEffectiveNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (LpPartitionState state in _partitions)
        {
            string effectiveName = GetEffectivePartitionName(state);
            if (!partitionRawNames.Add(state.RawName) ||
                !partitionEffectiveNames.Add(effectiveName))
            {
                throw new InvalidDataException(
                    Resources.FormatDuplicateName(state.RawName));
            }
        }

        var groupRawNames = new HashSet<string>(StringComparer.Ordinal);
        var groupEffectiveNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (LpGroupState state in _groups)
        {
            string effectiveName = LpNameValidator.GetEffectiveName(
                state.RawName,
                (state.Flags & LpGroupFlags.SlotSuffixed) != 0,
                _session.SlotNumber);
            if (!groupRawNames.Add(state.RawName) ||
                !groupEffectiveNames.Add(effectiveName))
            {
                throw new InvalidDataException(
                    Resources.FormatDuplicateName(state.RawName));
            }
        }
    }

    private static void ThrowIfDefault(LpGroupState group)
    {
        if (group.IsDefault)
        {
            throw new InvalidOperationException(Resources.DefaultGroupProtected);
        }
    }
}
